// Package transport держит двунаправленный поток с центром: отправляет
// сообщения агента, принимает подтверждения и настройки, переподключается.
package transport

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"math/rand/v2"
	"sync/atomic"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/keepalive"
	"google.golang.org/protobuf/types/known/timestamppb"

	"icc/agent/internal/enroll"
	agentv1 "icc/agent/internal/pb/icc/agent/v1"
)

// Queue — сколько сообщений агент держит в памяти, пока нет связи.
// Дисковый буфер — следующий этап; при переполнении отбрасываются новые метрики.
const Queue = 5000

type Client struct {
	center   string
	identity *enroll.Identity
	version  string
	host     func() *agentv1.HostInfo
	onConfig func(*agentv1.AgentConfig)
	log      *slog.Logger

	out     chan *agentv1.ConnectRequest
	dropped atomic.Uint64
	online  atomic.Bool
}

func New(center string, identity *enroll.Identity, version string, host func() *agentv1.HostInfo,
	onConfig func(*agentv1.AgentConfig), log *slog.Logger) *Client {
	return &Client{
		center: center, identity: identity, version: version, host: host, onConfig: onConfig, log: log,
		out: make(chan *agentv1.ConnectRequest, Queue),
	}
}

// Send ставит сообщение в очередь, не блокируясь. false — очередь переполнена.
func (c *Client) Send(m *agentv1.ConnectRequest) bool {
	select {
	case c.out <- m:
		return true
	default:
		c.dropped.Add(1)
		return false
	}
}

func (c *Client) Buffered() int   { return len(c.out) }
func (c *Client) Dropped() uint64 { return c.dropped.Load() }
func (c *Client) Online() bool    { return c.online.Load() }

// Run держит соединение, пока не отменён ctx.
func (c *Client) Run(ctx context.Context) {
	backoff := time.Second
	for ctx.Err() == nil {
		started := time.Now()
		err := c.session(ctx)
		c.online.Store(false)
		if ctx.Err() != nil {
			return
		}
		if time.Since(started) > time.Minute {
			backoff = time.Second
		}
		wait := backoff/2 + time.Duration(rand.Int64N(int64(backoff)))
		c.log.Warn("connection to center lost", "error", err, "retry_in", wait.Round(time.Millisecond))
		select {
		case <-ctx.Done():
			return
		case <-time.After(wait):
		}
		backoff = min(backoff*2, 30*time.Second)
	}
}

func (c *Client) session(ctx context.Context) error {
	conn, err := grpc.NewClient(c.center,
		grpc.WithTransportCredentials(credentials.NewTLS(c.identity.TLSConfig())),
		grpc.WithKeepaliveParams(keepalive.ClientParameters{Time: 30 * time.Second, Timeout: 10 * time.Second}),
	)
	if err != nil {
		return err
	}
	defer conn.Close()

	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	stream, err := agentv1.NewAgentServiceClient(conn).Connect(ctx)
	if err != nil {
		return err
	}

	var seq uint64
	send := func(m *agentv1.ConnectRequest) error {
		seq++
		m.Seq = seq
		m.SentAt = timestamppb.Now()
		return stream.Send(m)
	}

	if err := send(&agentv1.ConnectRequest{Payload: &agentv1.ConnectRequest_Hello{Hello: &agentv1.Hello{
		AgentId: c.identity.AgentID, AgentVersion: c.version, Host: c.host(),
	}}}); err != nil {
		return fmt.Errorf("send hello: %w", err)
	}
	first, err := stream.Recv()
	if err != nil {
		return fmt.Errorf("await hello ack: %w", err)
	}
	ack := first.GetHelloAck()
	if ack == nil {
		return errors.New("center did not answer with HelloAck")
	}
	c.onConfig(ack.GetConfig())
	c.online.Store(true)
	c.log.Info("connected to center", "center", c.center, "agent_id", c.identity.AgentID)

	recvErr := make(chan error, 1)
	go func() {
		for {
			msg, err := stream.Recv()
			if err != nil {
				recvErr <- err
				return
			}
			if cmd := msg.GetCommand(); cmd != nil {
				// Выполнение команд — после диплома; пока только фиксируем.
				c.log.Info("command received but not supported yet", "id", cmd.GetId())
			}
		}
	}()

	for {
		select {
		case <-ctx.Done():
			_ = stream.CloseSend()
			return ctx.Err()
		case err := <-recvErr:
			return err
		case m := <-c.out:
			if err := send(m); err != nil {
				// Сообщение, на котором оборвалась связь, возвращаем в очередь.
				c.Send(m)
				return err
			}
		}
	}
}
