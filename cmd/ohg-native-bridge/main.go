package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"os"
	"path/filepath"

	"github.com/beilunyang/openhongguo/adapters"
	"github.com/beilunyang/openhongguo/adapters/filestore"
	"github.com/beilunyang/openhongguo/internal/history"
	"github.com/beilunyang/openhongguo/internal/nativebridge"
	"github.com/beilunyang/openhongguo/sdk"
)

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "原生接口组件启动失败")
		os.Exit(1)
	}
}

func run() error {
	configDir := flag.String("config-dir", "", "原生版配置目录")
	flag.Parse()
	if *configDir == "" {
		base, err := os.UserConfigDir()
		if err != nil {
			return err
		}
		*configDir = filepath.Join(base, "openhongguo")
	}
	if err := os.MkdirAll(*configDir, 0700); err != nil {
		return err
	}
	file, err := os.OpenFile(filepath.Join(*configDir, "native-backend.log"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600)
	if err != nil {
		return err
	}
	defer file.Close()
	client, err := adapters.NewClient("", sdk.Options{ProfileStore: filestore.New(filepath.Join(*configDir, "anonymous.json")), RetryAttempts: 2})
	if err != nil {
		return err
	}
	server := &nativebridge.Server{Service: client, History: history.NewStore(filepath.Join(*configDir, "history.json")), ConfigDir: *configDir, Logger: log.New(file, "", log.LstdFlags)}
	return server.Serve(context.Background(), os.Stdin, os.Stdout)
}
