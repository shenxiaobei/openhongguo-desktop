//go:build !windows

package desktop

import (
	"fmt"
	"os"
	"os/exec"
	"runtime"
)

func openWindow(url, _ string) error {
	name := "xdg-open"
	if runtime.GOOS == "darwin" {
		name = "open"
	}
	command := exec.Command(name, url)
	if err := command.Start(); err != nil {
		return fmt.Errorf("打开浏览器失败，请手动访问 %s：%w", url, err)
	}
	return command.Process.Release()
}

// ShowError 为终端环境保留清楚的启动错误输出。
func ShowError(message string) {
	fmt.Fprintln(os.Stderr, "红果桌面版启动失败：", message)
}
