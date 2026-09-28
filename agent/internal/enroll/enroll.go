// Package enroll регистрирует агента в центре и хранит его личность:
// закрытый ключ, сертификат агента и сертификат CA центра.
package enroll

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials"

	agentv1 "icc/agent/internal/pb/icc/agent/v1"
)

// Token — токен регистрации: icc1.<id>.<secret>.<sha256 CA>.
type Token struct {
	ID     string
	Secret string
	CAHash string
}

func ParseToken(s string) (Token, error) {
	parts := strings.Split(strings.TrimSpace(s), ".")
	if len(parts) != 4 || parts[0] != "icc1" || len(parts[1]) != 8 || len(parts[2]) != 32 || len(parts[3]) != 64 {
		return Token{}, errors.New("malformed join token: expected icc1.<id>.<secret>.<ca-hash>")
	}
	if _, err := hex.DecodeString(parts[3]); err != nil {
		return Token{}, fmt.Errorf("malformed CA hash in token: %w", err)
	}
	return Token{ID: parts[1], Secret: parts[2], CAHash: strings.ToLower(parts[3])}, nil
}

func (t Token) String() string { return "icc1." + t.ID + "." + t.Secret + "." + t.CAHash }

// Identity — всё, что нужно агенту для mTLS-соединения с центром.
type Identity struct {
	AgentID string
	Cert    tls.Certificate
	CA      *x509.Certificate
}

const (
	keyFile  = "agent.key"
	certFile = "agent.crt"
	caFile   = "ca.crt"
	metaFile = "agent.json"
)

type meta struct {
	AgentID string `json:"agent_id"`
	Center  string `json:"center"`
}

// Load читает сохранённую личность. os.ErrNotExist — агент ещё не зарегистрирован.
func Load(dir string) (*Identity, error) {
	raw, err := os.ReadFile(filepath.Join(dir, metaFile))
	if err != nil {
		return nil, err
	}
	var m meta
	if err := json.Unmarshal(raw, &m); err != nil {
		return nil, fmt.Errorf("read %s: %w", metaFile, err)
	}
	cert, err := tls.LoadX509KeyPair(filepath.Join(dir, certFile), filepath.Join(dir, keyFile))
	if err != nil {
		return nil, fmt.Errorf("load agent certificate: %w", err)
	}
	caPEM, err := os.ReadFile(filepath.Join(dir, caFile))
	if err != nil {
		return nil, err
	}
	ca, err := parseCertPEM(caPEM)
	if err != nil {
		return nil, fmt.Errorf("parse CA: %w", err)
	}
	return &Identity{AgentID: m.AgentID, Cert: cert, CA: ca}, nil
}

// Register выполняет регистрацию по токену и сохраняет личность в dir.
func Register(ctx context.Context, center string, token Token, host *agentv1.HostInfo, version, dir string) (*Identity, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, err
	}
	csrDER, err := x509.CreateCertificateRequest(rand.Reader, &x509.CertificateRequest{
		Subject: pkix.Name{CommonName: host.GetHostname()},
	}, key)
	if err != nil {
		return nil, err
	}

	ca, err := FetchCA(ctx, center, token.CAHash)
	if err != nil {
		return nil, err
	}
	conn, err := grpc.NewClient(center, grpc.WithTransportCredentials(credentials.NewTLS(&tls.Config{
		MinVersion:            tls.VersionTLS12,
		InsecureSkipVerify:    true, //nolint:gosec // проверка выполняется в VerifyPeerCertificate
		VerifyPeerCertificate: verifyWithCA(ca),
	})))
	if err != nil {
		return nil, err
	}
	defer conn.Close()

	resp, err := agentv1.NewAgentServiceClient(conn).Register(ctx, &agentv1.RegisterRequest{
		Token:        token.String(),
		CsrPem:       string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE REQUEST", Bytes: csrDER})),
		Host:         host,
		AgentVersion: version,
	})
	if err != nil {
		return nil, fmt.Errorf("register: %w", err)
	}

	if returned, err := parseCertPEM([]byte(resp.GetCaCertPem())); err != nil || hashOf(returned) != token.CAHash {
		return nil, errors.New("center returned a CA that does not match the join token")
	}

	keyDER, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		return nil, err
	}
	keyPEM := pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyDER})
	cert, err := tls.X509KeyPair([]byte(resp.GetClientCertPem()), keyPEM)
	if err != nil {
		return nil, fmt.Errorf("agent certificate does not match key: %w", err)
	}

	if err := os.MkdirAll(dir, 0o700); err != nil {
		return nil, err
	}
	files := map[string][]byte{
		keyFile:  keyPEM,
		certFile: []byte(resp.GetClientCertPem()),
		caFile:   []byte(resp.GetCaCertPem()),
	}
	metaJSON, _ := json.MarshalIndent(meta{AgentID: resp.GetAgentId(), Center: center}, "", "  ")
	files[metaFile] = metaJSON
	for name, data := range files {
		if err := os.WriteFile(filepath.Join(dir, name), data, 0o600); err != nil {
			return nil, err
		}
	}
	return &Identity{AgentID: resp.GetAgentId(), Cert: cert, CA: ca}, nil
}

// TLSConfig — mTLS к центру: клиентский сертификат агента и проверка сервера по CA центра.
// Имя хоста не проверяется: центр доступен по любому адресу, доверие — к CA.
func (id *Identity) TLSConfig() *tls.Config {
	return &tls.Config{
		MinVersion:            tls.VersionTLS12,
		Certificates:          []tls.Certificate{id.Cert},
		InsecureSkipVerify:    true, //nolint:gosec // проверка выполняется в VerifyPeerCertificate
		VerifyPeerCertificate: verifyWithCA(id.CA),
	}
}

// FetchCA получает сертификат CA центра и сверяет его хеш с токеном. Соединение
// на этом шаге не проверяется: подменённый CA не совпадёт с хешем из токена.
func FetchCA(ctx context.Context, center, caHash string) (*x509.Certificate, error) {
	conn, err := grpc.NewClient(center, grpc.WithTransportCredentials(credentials.NewTLS(&tls.Config{
		MinVersion:         tls.VersionTLS12,
		InsecureSkipVerify: true, //nolint:gosec // доверие устанавливается сверкой хеша CA ниже
	})))
	if err != nil {
		return nil, err
	}
	defer conn.Close()
	resp, err := agentv1.NewAgentServiceClient(conn).GetCenterInfo(ctx, &agentv1.GetCenterInfoRequest{})
	if err != nil {
		return nil, fmt.Errorf("get center info: %w", err)
	}
	ca, err := parseCertPEM([]byte(resp.GetCaCertPem()))
	if err != nil {
		return nil, fmt.Errorf("parse CA from center: %w", err)
	}
	if hashOf(ca) != caHash {
		return nil, errors.New("center CA does not match the join token: wrong center or token")
	}
	return ca, nil
}

// verifyWithCA проверяет, что серверный сертификат выпущен CA центра.
// Имя хоста не проверяется: доверие — к CA, а не к адресу.
func verifyWithCA(ca *x509.Certificate) func([][]byte, [][]*x509.Certificate) error {
	roots := x509.NewCertPool()
	roots.AddCert(ca)
	return func(rawCerts [][]byte, _ [][]*x509.Certificate) error {
		if len(rawCerts) == 0 {
			return errors.New("center presented no certificate")
		}
		leaf, err := x509.ParseCertificate(rawCerts[0])
		if err != nil {
			return err
		}
		intermediates := x509.NewCertPool()
		for _, raw := range rawCerts[1:] {
			if c, err := x509.ParseCertificate(raw); err == nil {
				intermediates.AddCert(c)
			}
		}
		_, err = leaf.Verify(x509.VerifyOptions{
			Roots: roots, Intermediates: intermediates,
			KeyUsages: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		})
		return err
	}
}

func parseCertPEM(data []byte) (*x509.Certificate, error) {
	block, _ := pem.Decode(data)
	if block == nil || block.Type != "CERTIFICATE" {
		return nil, errors.New("no PEM certificate")
	}
	return x509.ParseCertificate(block.Bytes)
}

func hashOf(c *x509.Certificate) string { return hex.EncodeToString(sha256Of(c.Raw)) }

func sha256Of(b []byte) []byte {
	h := sha256.Sum256(b)
	return h[:]
}
