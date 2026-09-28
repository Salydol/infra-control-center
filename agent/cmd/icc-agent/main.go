// Command icc-agent — агент ICC: собирает метрики, логи и события изменений
// с хоста и контейнеров и передаёт их в центр по исходящему gRPC-потоку.
package main

import (
	"flag"
	"fmt"
	"log/slog"
	"os"
)

// version задаётся при сборке: -ldflags "-X main.version=..."
var version = "dev"

func main() {
	var (
		centerAddr  = flag.String("center", "localhost:9090", "адрес gRPC-endpoint центра")
		showVersion = flag.Bool("version", false, "показать версию и выйти")
	)
	flag.Parse()

	if *showVersion {
		fmt.Println(version)
		return
	}

	logger := slog.New(slog.NewJSONHandler(os.Stdout, nil))
	logger.Info("icc-agent запущен", "version", version, "center", *centerAddr)
}
