package media

import (
	"context"
	"errors"
	"fmt"
	"os"
	"strings"
	"sync"
	"testing"
	"time"
)

// 使用当前测试程序充当播放器，避免依赖 Windows 不支持的 shell 脚本桩。
func TestPlayerCancellationHelper(t *testing.T) {
	if os.Getenv("OHG_TEST_PLAYER_HELPER") != "1" {
		return
	}
	fmt.Fprint(os.Stderr, "0.00 M-A: 0.000 fd=0\r12.50 M-A: 0.000 fd=0\rohg-player-ready\n")
	for {
		time.Sleep(time.Hour)
	}
}

type playerReadyWriter struct {
	ready chan struct{}
	once  sync.Once
}

func (w *playerReadyWriter) Write(data []byte) (int, error) {
	if strings.Contains(string(data), "ohg-player-ready") {
		w.once.Do(func() { close(w.ready) })
	}
	return len(data), nil
}

func TestCancelPlayerPreservesLastReportedPosition(t *testing.T) {
	program, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	t.Setenv("OHG_TEST_PLAYER_HELPER", "1")
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	ready := &playerReadyWriter{ready: make(chan struct{})}
	launcher := &Launcher{Err: ready}
	type outcome struct {
		result Result
		err    error
	}
	done := make(chan outcome, 1)
	go func() {
		result, err := launcher.runPlayer(ctx, "ffplay", program, []string{"-test.run=^TestPlayerCancellationHelper$"}, Request{})
		done <- outcome{result: result, err: err}
	}()
	select {
	case <-ready.ready:
	case result := <-done:
		t.Fatalf("播放器提前退出：%v", result.err)
	case <-time.After(10 * time.Second):
		t.Fatal("测试播放器没有报告进度")
	}
	cancel()
	select {
	case result := <-done:
		if !errors.Is(result.err, context.Canceled) || result.result.Position != 12.5 {
			t.Fatalf("取消后进度丢失：position=%v err=%v", result.result.Position, result.err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("取消后播放器未退出")
	}
}
