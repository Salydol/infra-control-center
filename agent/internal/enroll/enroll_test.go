package enroll

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"crypto/x509/pkix"
	"math/big"
	"strings"
	"testing"
	"time"
)

func TestParseToken(t *testing.T) {
	valid := "icc1.0a1b2c3d." + strings.Repeat("f", 32) + "." + strings.Repeat("A", 64)
	tok, err := ParseToken(valid)
	if err != nil {
		t.Fatal(err)
	}
	if tok.ID != "0a1b2c3d" || tok.CAHash != strings.Repeat("a", 64) {
		t.Fatalf("unexpected token %+v", tok)
	}

	for _, bad := range []string{
		"",
		"icc2.0a1b2c3d." + strings.Repeat("f", 32) + "." + strings.Repeat("a", 64),
		"icc1.short." + strings.Repeat("f", 32) + "." + strings.Repeat("a", 64),
		"icc1.0a1b2c3d." + strings.Repeat("f", 32) + "." + strings.Repeat("z", 64),
	} {
		if _, err := ParseToken(bad); err == nil {
			t.Errorf("token %q should be rejected", bad)
		}
	}
}

type testCert struct {
	cert *x509.Certificate
	key  *ecdsa.PrivateKey
}

func newCert(t *testing.T, cn string, isCA bool, issuer *testCert, usage x509.ExtKeyUsage) testCert {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	tmpl := &x509.Certificate{
		SerialNumber:          big.NewInt(time.Now().UnixNano()),
		Subject:               pkix.Name{CommonName: cn},
		NotBefore:             time.Now().Add(-time.Hour),
		NotAfter:              time.Now().Add(time.Hour),
		IsCA:                  isCA,
		BasicConstraintsValid: true,
	}
	if isCA {
		tmpl.KeyUsage = x509.KeyUsageCertSign
	} else {
		tmpl.KeyUsage = x509.KeyUsageDigitalSignature
		tmpl.ExtKeyUsage = []x509.ExtKeyUsage{usage}
	}
	parent, signer := tmpl, key
	if issuer != nil {
		parent, signer = issuer.cert, issuer.key
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, parent, &key.PublicKey, signer)
	if err != nil {
		t.Fatal(err)
	}
	cert, _ := x509.ParseCertificate(der)
	return testCert{cert: cert, key: key}
}

func TestVerifyWithCA(t *testing.T) {
	ca := newCert(t, "ICC Agent CA", true, nil, 0)
	server := newCert(t, "ICC Control Plane", false, &ca, x509.ExtKeyUsageServerAuth)
	otherCA := newCert(t, "Other CA", true, nil, 0)
	foreignServer := newCert(t, "Evil", false, &otherCA, x509.ExtKeyUsageServerAuth)
	clientOnly := newCert(t, "Client", false, &ca, x509.ExtKeyUsageClientAuth)

	cases := []struct {
		name  string
		chain [][]byte
		ok    bool
	}{
		{"server signed by center CA", [][]byte{server.cert.Raw}, true},
		{"server signed by another CA", [][]byte{foreignServer.cert.Raw}, false},
		{"another CA smuggled into chain", [][]byte{foreignServer.cert.Raw, otherCA.cert.Raw}, false},
		{"leaf without serverAuth", [][]byte{clientOnly.cert.Raw}, false},
		{"empty chain", nil, false},
	}
	for _, tc := range cases {
		err := verifyWithCA(ca.cert)(tc.chain, nil)
		if (err == nil) != tc.ok {
			t.Errorf("%s: err=%v, want ok=%v", tc.name, err, tc.ok)
		}
	}
}
