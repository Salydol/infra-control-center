// Command icc-agent — агент ICC: собирает метрики, логи и события изменений
// с хоста и контейнеров и передаёт их в центр по исходящему gRPC-потоку.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io/fs"
	"log/slog"
	"os"
	"os/signal"
	"syscall"
	"time"

	"icc/agent/internal/agent"
	"icc/agent/internal/collect"
	"icc/agent/internal/enroll"
)

// version задаётся при сборке: -ldflags "-X main.version=..."
var version = "dev"

func main() {
	var (
		center      = flag.String("center", env("ICC_CENTER", "localhost:9090"), "адрес gRPC-endpoint центра (host:port)")
		token       = flag.String("token", env("ICC_TOKEN", ""), "токен регистрации (нужен только при первом запуске)")
		dataDir     = flag.String("data-dir", env("ICC_DATA_DIR", "/var/lib/icc-agent"), "каталог ключей и сертификатов агента")
		showVersion = flag.Bool("version", false, "показать версию и выйти")
	)
	flag.Parse()

	if *showVersion {
		fmt.Println(version)
		return
	}

	log := slog.New(slog.NewJSONHandler(os.Stdout, nil))
	if err := run(*center, *token, *dataDir, log); err != nil {
		log.Error("agent stopped", "error", err)
		os.Exit(1)
	}
}

func run(center, token, dataDir string, log *slog.Logger) error {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	docker, err := collect.NewDocker(collect.DefaultIgnore())
	if err != nil {
		return fmt.Errorf("docker client: %w", err)
	}
	defer docker.Close()
	host := collect.HostInfo(ctx, docker.Version(ctx))

	identity, err := enroll.Load(dataDir)
	switch {
	case err == nil:
		log.Info("loaded agent identity", "agent_id", identity.AgentID)
	case errors.Is(err, fs.ErrNotExist):
		if token == "" {
			return errors.New("agent is not registered: pass --token (or ICC_TOKEN) from `agents create-token`")
		}
		parsed, err := enroll.ParseToken(token)
		if err != nil {
			return err
		}
		regCtx, cancel := context.WithTimeout(ctx, 30*time.Second)
		identity, err = enroll.Register(regCtx, center, parsed, host, version, dataDir)
		cancel()
		if err != nil {
			return err
		}
		log.Info("registered in center", "agent_id", identity.AgentID, "center", center)
	default:
		return fmt.Errorf("load identity: %w", err)
	}

	log.Info("icc-agent started", "version", version, "center", center, "host", host.GetHostname())
	agent.New(center, version, identity, docker, host, log).Run(ctx)
	return nil
}

func env(name, fallback string) string {
	if v, ok := os.LookupEnv(name); ok && v != "" {
		return v
	}
	return fallback
}
