package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/beilunyang/openhongguo/internal/desktop"
)

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "红果桌面版：", err)
		desktop.ShowError(err.Error())
		os.Exit(1)
	}
}

func run() error {
	options := desktop.Options{}
	flags := flag.NewFlagSet("红果桌面版", flag.ContinueOnError)
	flags.StringVar(&options.ConfigDir, "config-dir", os.Getenv("OHG_CONFIG_DIR"), "配置和日志目录")
	flags.StringVar(&options.ListenAddr, "listen", "127.0.0.1:0", "本机监听地址，端口 0 表示自动分配")
	flags.BoolVar(&options.NoBrowser, "no-browser", false, "仅启动本机服务，供诊断使用")
	flags.DurationVar(&options.IdleTimeout, "idle-timeout", 90*time.Second, "界面关闭后的空闲退出时间")
	if err := flags.Parse(os.Args[1:]); err != nil {
		if errors.Is(err, flag.ErrHelp) {
			return nil
		}
		return err
	}
	if flags.NArg() != 0 {
		return fmt.Errorf("多余的启动参数：%s", flags.Arg(0))
	}

	// 统一取消服务和播放器，避免退出后遗留后台任务。
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	return desktop.Run(ctx, options)
}
