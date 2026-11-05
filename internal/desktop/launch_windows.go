//go:build windows

package desktop

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
	"unsafe"
)

func openWindow(url, configDir string) error {
	profileDir := filepath.Join(configDir, "desktop-browser")
	if err := os.MkdirAll(profileDir, 0700); err != nil {
		return fmt.Errorf("创建界面配置目录失败：%w", err)
	}

	// 使用独立的浏览器窗口与配置，不接触用户日常浏览器会话。
	for _, base := range []string{os.Getenv("ProgramFiles(x86)"), os.Getenv("ProgramFiles"), os.Getenv("LOCALAPPDATA")} {
		if base == "" {
			continue
		}
		edgePath := filepath.Join(base, "Microsoft", "Edge", "Application", "msedge.exe")
		if _, err := os.Stat(edgePath); err != nil {
			continue
		}
		command := exec.Command(edgePath,
			"--app="+url,
			"--window-size=1280,860",
			"--user-data-dir="+profileDir,
			"--no-first-run",
		)
		if err := startWindowProcess(command); err == nil {
			return nil
		}
	}

	// 缺少 Edge 时由系统默认浏览器打开，参数直接传给进程而不经过命令解释器。
	systemRoot := os.Getenv("SystemRoot")
	if systemRoot == "" {
		systemRoot = `C:\Windows`
	}
	command := exec.Command(filepath.Join(systemRoot, "System32", "rundll32.exe"), "url.dll,FileProtocolHandler", url)
	if err := startWindowProcess(command); err != nil {
		return fmt.Errorf("打开桌面界面失败，请检查 Edge 或默认浏览器：%w", err)
	}
	return nil
}

func startWindowProcess(command *exec.Cmd) error {
	// 仅抑制控制台窗口，保留浏览器本身的可见窗口。
	command.SysProcAttr = &syscall.SysProcAttr{CreationFlags: 0x08000000}
	if err := command.Start(); err != nil {
		return err
	}
	return command.Process.Release()
}

// ShowError 在无控制台的发布版本中显示启动错误，避免无声退出。
func ShowError(message string) {
	text, err := syscall.UTF16PtrFromString(message)
	if err != nil {
		text, _ = syscall.UTF16PtrFromString("启动失败，请查看配置目录中的日志。")
	}
	title, _ := syscall.UTF16PtrFromString("红果桌面版启动失败")
	messageBox := syscall.NewLazyDLL("user32.dll").NewProc("MessageBoxW")
	_, _, _ = messageBox.Call(0, uintptr(unsafe.Pointer(text)), uintptr(unsafe.Pointer(title)), 0x00010010)
}
