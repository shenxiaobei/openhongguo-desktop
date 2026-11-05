// Package desktop 为现有 SDK 提供可双击启动的本地桌面界面。
package desktop

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"embed"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"github.com/beilunyang/openhongguo/adapters"
	"github.com/beilunyang/openhongguo/adapters/filestore"
	"github.com/beilunyang/openhongguo/internal/history"
	"github.com/beilunyang/openhongguo/internal/media"
	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

// Version 可在打包时覆盖，用于界面和问题定位。
var Version = "0.2.0-desktop.1"

//go:embed web/*
var webFiles embed.FS

// Options 控制桌面入口；零值即可正常启动。
type Options struct {
	ConfigDir   string
	ListenAddr  string
	NoBrowser   bool
	IdleTimeout time.Duration
}

// Service 只保留桌面界面实际使用的 SDK 能力。
type Service interface {
	Discover(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error)
	Search(context.Context, string, sdk.SearchOptions) (*sdk.Page[model.SeriesSummary], error)
	GetSeriesBatch(context.Context, []string) ([]model.SeriesDetail, error)
	ResolvePlayback(context.Context, string, sdk.PlaybackOptions) (*model.Playback, error)
}

type player interface {
	Play(context.Context, media.Request) (media.Result, error)
}

type toolInfo struct {
	Name      string `json:"name"`
	Path      string `json:"path"`
	Available bool   `json:"available"`
}

type playbackStatus struct {
	State        string `json:"state"`
	SeriesID     string `json:"series_id,omitempty"`
	EpisodeID    string `json:"episode_id,omitempty"`
	Title        string `json:"title,omitempty"`
	EpisodeTitle string `json:"episode_title,omitempty"`
	Error        string `json:"error,omitempty"`
}

type playRequest struct {
	SeriesID     string `json:"series_id"`
	EpisodeID    string `json:"episode_id"`
	Title        string `json:"title"`
	EpisodeTitle string `json:"episode_title"`
	Quality      string `json:"quality"`
}

type cachedDetail struct {
	detail model.SeriesDetail
	at     time.Time
}

type app struct {
	ctx            context.Context
	service        Service
	history        *history.Store
	player         player
	configDir      string
	logFile        string
	logger         *log.Logger
	tools          []toolInfo
	shutdown       context.CancelFunc
	mu             sync.Mutex
	playback       playbackStatus
	playCancel     context.CancelFunc
	playDone       chan struct{}
	playGeneration uint64
	lastPing       time.Time
	seriesCache    map[string]cachedDetail
}

// Run 启动仅绑定本机的服务，再打开独立窗口；启动错误交给入口显式显示。
func Run(ctx context.Context, opts Options) error {
	configDir := opts.ConfigDir
	if configDir == "" {
		base, err := os.UserConfigDir()
		if err != nil {
			return fmt.Errorf("读取配置目录失败：%w", err)
		}
		configDir = filepath.Join(base, "openhongguo")
	}
	configDir, err := filepath.Abs(configDir)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(configDir, 0700); err != nil {
		return fmt.Errorf("创建配置目录失败：%w", err)
	}
	logPath := filepath.Join(configDir, "desktop.log")
	logFile, err := os.OpenFile(logPath, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600)
	if err != nil {
		return fmt.Errorf("打开运行日志失败：%w", err)
	}
	defer logFile.Close()
	logger := log.New(logFile, "", log.LstdFlags)
	logger.Printf("桌面版启动 version=%s", Version)

	client, err := adapters.NewClient("", sdk.Options{
		ProfileStore:       filestore.New(filepath.Join(configDir, "anonymous.json")),
		APIOrigin:          os.Getenv("OHG_API_ORIGIN"),
		RegisterOrigin:     os.Getenv("OHG_REGISTER_ORIGIN"),
		DistributionOrigin: os.Getenv("OHG_DISTRIBUTION_ORIGIN"),
		RetryAttempts:      2,
	})
	if err != nil {
		return fmt.Errorf("初始化客户端失败：%w", err)
	}
	addr := opts.ListenAddr
	if addr == "" {
		addr = "127.0.0.1:0"
	}
	host, _, err := net.SplitHostPort(addr)
	if err != nil || net.ParseIP(host) == nil || !net.ParseIP(host).IsLoopback() {
		return errors.New("桌面服务地址必须是本机回环地址，例如 127.0.0.1:0")
	}
	listener, err := net.Listen("tcp", addr)
	if err != nil {
		return fmt.Errorf("启动桌面服务失败：%w", err)
	}
	defer listener.Close()
	tokenBytes := make([]byte, 32)
	if _, err := rand.Read(tokenBytes); err != nil {
		return err
	}
	token := hex.EncodeToString(tokenBytes)
	origin := "http://" + listener.Addr().String()
	launchURL := origin + "/?session=" + token
	appCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	tools := detectTools()
	launcher := media.New()
	// 媒体进程可能在输出中回显播放参数；不把这些内容写入桌面日志。
	launcher.Out = io.Discard
	launcher.Err = io.Discard
	launcher.LookPath = func(name string) (string, error) {
		for _, tool := range tools {
			if tool.Name == name && tool.Available {
				return tool.Path, nil
			}
		}
		return "", exec.ErrNotFound
	}
	a := &app{
		ctx: appCtx, service: client, player: launcher,
		history:   history.NewStore(filepath.Join(configDir, "history.json")),
		configDir: configDir, logFile: logPath, logger: logger, tools: tools,
		shutdown: cancel, playback: playbackStatus{State: "idle"},
		lastPing: time.Now(), seriesCache: make(map[string]cachedDetail),
	}
	server := &http.Server{
		Handler: a.handler(origin, token), ReadHeaderTimeout: 5 * time.Second,
		IdleTimeout: 60 * time.Second, MaxHeaderBytes: 16 << 10,
		ErrorLog: logger,
	}
	serveErr := make(chan error, 1)
	go func() { serveErr <- server.Serve(listener) }()
	logger.Printf("桌面服务已监听 %s", origin)
	if opts.NoBrowser {
		// 调试模式显式输出一次启动链接；日志只记录不含会话的地址。
		fmt.Fprintln(os.Stdout, launchURL)
	} else if err := openWindow(launchURL, configDir); err != nil {
		server.Close()
		logger.Print("打开桌面窗口失败")
		return fmt.Errorf("打开桌面窗口失败：%w；日志：%s", err, logPath)
	}
	idleTimeout := opts.IdleTimeout
	if idleTimeout <= 0 {
		idleTimeout = 90 * time.Second
	}
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	var runErr error
	running := true
	for running {
		select {
		case <-appCtx.Done():
			running = false
		case err := <-serveErr:
			if !errors.Is(err, http.ErrServerClosed) {
				runErr = err
			}
			running = false
		case <-ticker.C:
			a.mu.Lock()
			idle := time.Since(a.lastPing)
			a.mu.Unlock()
			if idle > idleTimeout {
				logger.Print("窗口心跳结束，释放桌面服务")
				running = false
			}
		}
	}
	cancel()
	a.stopPlayback()
	shutdownCtx, stop := context.WithTimeout(context.Background(), 5*time.Second)
	defer stop()
	if err := server.Shutdown(shutdownCtx); err != nil {
		server.Close()
	}
	a.mu.Lock()
	playDone := a.playDone
	a.mu.Unlock()
	if playDone != nil {
		select {
		case <-playDone:
		case <-shutdownCtx.Done():
		}
	}
	logger.Print("桌面版已退出")
	return runErr
}

func (a *app) handler(origin, token string) http.Handler {
	base, _ := url.Parse(origin)
	// Cookie 不按端口隔离；各窗口使用不同名称，避免再次双击挤掉已有会话。
	cookieName := "ohg_session_" + base.Port()
	assets, _ := fs.Sub(webFiles, "web")
	files := http.FileServer(http.FS(assets))
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("Referrer-Policy", "no-referrer")
		w.Header().Set("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' https: http: data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
		if r.Host != base.Host {
			writeError(w, http.StatusForbidden, "HOST_REJECTED", "此地址不属于当前桌面会话")
			return
		}
		if r.Method == http.MethodGet && r.URL.Path == "/" && r.URL.Query().Has("session") {
			if !sameToken(r.URL.Query().Get("session"), token) {
				writeError(w, http.StatusUnauthorized, "SESSION_EXPIRED", "会话已结束，请重新打开桌面版")
				return
			}
			http.SetCookie(w, &http.Cookie{Name: cookieName, Value: token, Path: "/", HttpOnly: true, SameSite: http.SameSiteStrictMode})
			http.Redirect(w, r, "/", http.StatusSeeOther)
			return
		}
		cookie, err := r.Cookie(cookieName)
		if err != nil || !sameToken(cookie.Value, token) {
			writeError(w, http.StatusUnauthorized, "SESSION_EXPIRED", "会话已结束，请重新打开桌面版")
			return
		}
		if r.Method != http.MethodGet && r.Header.Get("Origin") != origin {
			writeError(w, http.StatusForbidden, "ORIGIN_REJECTED", "请求来源与当前桌面窗口不符")
			return
		}
		if strings.HasPrefix(r.URL.Path, "/api/") {
			a.handleAPI(w, r)
			return
		}
		if r.Method != http.MethodGet {
			writeError(w, http.StatusMethodNotAllowed, "METHOD_NOT_ALLOWED", "此操作不支持该请求方法")
			return
		}
		switch r.URL.Path {
		case "/favicon.ico":
			w.WriteHeader(http.StatusNoContent)
		case "/", "/app.js", "/style.css":
			files.ServeHTTP(w, r)
		default:
			http.NotFound(w, r)
		}
	})
}

func sameToken(value, expected string) bool {
	return len(value) == len(expected) && subtle.ConstantTimeCompare([]byte(value), []byte(expected)) == 1
}

func (a *app) handleAPI(w http.ResponseWriter, r *http.Request) {
	method := http.MethodGet
	switch r.URL.Path {
	case "/api/play", "/api/stop", "/api/shutdown", "/api/ping":
		method = http.MethodPost
	}
	if r.Method != method {
		writeError(w, http.StatusMethodNotAllowed, "METHOD_NOT_ALLOWED", "此操作不支持该请求方法")
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), 40*time.Second)
	defer cancel()
	switch r.URL.Path {
	case "/api/status":
		writeJSON(w, http.StatusOK, map[string]any{
			"version": Version, "config_dir": a.configDir, "log_file": a.logFile,
			"player": a.selectedPlayer(), "tools": a.tools, "playback": a.snapshot(), "demo": false,
		})
	case "/api/ping":
		a.mu.Lock()
		a.lastPing = time.Now()
		a.mu.Unlock()
		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	case "/api/discover":
		page, err := a.service.Discover(ctx, sdk.DiscoverOptions{Channel: r.URL.Query().Get("channel"), Cursor: r.URL.Query().Get("cursor")})
		if err != nil {
			a.serviceError(w, err)
			return
		}
		writeJSON(w, http.StatusOK, normalizedPage(page))
	case "/api/search":
		query := strings.TrimSpace(r.URL.Query().Get("q"))
		if query == "" || len([]rune(query)) > 100 {
			writeError(w, http.StatusBadRequest, "INVALID_QUERY", "请输入 1 到 100 个字的搜索词")
			return
		}
		page, err := a.service.Search(ctx, query, sdk.SearchOptions{Cursor: r.URL.Query().Get("cursor"), PageSize: 20, Tab: "剧集"})
		if err != nil {
			a.serviceError(w, err)
			return
		}
		writeJSON(w, http.StatusOK, normalizedPage(page))
	case "/api/series":
		id := r.URL.Query().Get("id")
		if !validID(id) {
			writeError(w, http.StatusBadRequest, "INVALID_ID", "剧集编号格式不正确")
			return
		}
		detail, err := a.getDetail(ctx, id)
		if err != nil {
			a.serviceError(w, err)
			return
		}
		writeJSON(w, http.StatusOK, detail)
	case "/api/history":
		entries, err := a.history.List(ctx)
		if err != nil {
			writeError(w, http.StatusInternalServerError, "HISTORY_ERROR", "观看记录读取失败，请检查设置中的日志与配置目录")
			return
		}
		writeJSON(w, http.StatusOK, entries)
	case "/api/play":
		var req playRequest
		decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096))
		decoder.DisallowUnknownFields()
		if err := decoder.Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "INVALID_REQUEST", "播放请求格式不正确")
			return
		}
		if err := decoder.Decode(&struct{}{}); err != io.EOF {
			writeError(w, http.StatusBadRequest, "INVALID_REQUEST", "播放请求应仅包含一份数据")
			return
		}
		if !validID(req.SeriesID) || !validID(req.EpisodeID) || len(req.Title) > 1000 || len(req.EpisodeTitle) > 1000 {
			writeError(w, http.StatusBadRequest, "INVALID_ID", "请先选择剧集和要播放的集数")
			return
		}
		switch req.Quality {
		case "", "best", "worst", "360p", "480p", "540p", "720p", "1080p":
		default:
			writeError(w, http.StatusBadRequest, "INVALID_QUALITY", "请选择列表中的清晰度")
			return
		}
		if !a.selectedPlayer().Available {
			writeError(w, http.StatusConflict, "PLAYER_MISSING", "未检测到播放器，请安装 mpv、ffplay 或 VLC 后重新打开桌面版")
			return
		}
		a.startPlayback(req)
		writeJSON(w, http.StatusAccepted, map[string]bool{"ok": true})
	case "/api/stop":
		a.stopPlayback()
		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	case "/api/shutdown":
		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
		if a.shutdown != nil {
			a.shutdown()
		}
	default:
		writeError(w, http.StatusNotFound, "NOT_FOUND", "该操作不存在")
	}
}

func normalizedPage(page *sdk.Page[model.SeriesSummary]) *sdk.Page[model.SeriesSummary] {
	if page == nil {
		return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{}}
	}
	if page.Items == nil {
		page.Items = []model.SeriesSummary{}
	}
	return page
}

func (a *app) getDetail(ctx context.Context, id string) (model.SeriesDetail, error) {
	a.mu.Lock()
	cached, ok := a.seriesCache[id]
	a.mu.Unlock()
	if ok && time.Since(cached.at) < 5*time.Minute {
		return cached.detail, nil
	}
	details, err := a.service.GetSeriesBatch(ctx, []string{id})
	if err != nil {
		return model.SeriesDetail{}, err
	}
	for _, detail := range details {
		if detail.Series == nil || detail.Series.ID != id {
			continue
		}
		if detail.Episodes == nil {
			detail.Episodes = []model.Episode{}
		}
		a.mu.Lock()
		if a.seriesCache == nil || len(a.seriesCache) >= 64 {
			a.seriesCache = make(map[string]cachedDetail)
		}
		a.seriesCache[id] = cachedDetail{detail: detail, at: time.Now()}
		a.mu.Unlock()
		return detail, nil
	}
	return model.SeriesDetail{}, sdk.ErrContentUnavailable
}

func (a *app) snapshot() playbackStatus {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.playback.State == "" {
		return playbackStatus{State: "idle"}
	}
	return a.playback
}

func (a *app) startPlayback(req playRequest) {
	a.mu.Lock()
	if a.playCancel != nil {
		a.playCancel()
	}
	previousDone := a.playDone
	ctx, cancel := context.WithCancel(a.ctx)
	a.playGeneration++
	generation := a.playGeneration
	done := make(chan struct{})
	a.playCancel = cancel
	a.playDone = done
	a.playback = playbackStatus{State: "loading", SeriesID: req.SeriesID, EpisodeID: req.EpisodeID, Title: req.Title, EpisodeTitle: req.EpisodeTitle}
	a.mu.Unlock()
	go func() {
		defer close(done)
		defer cancel()
		// 等待上一播放器退出，防止快速切集留下多个播放器窗口。
		if previousDone != nil {
			<-previousDone
		}
		// 连续切三集时，中间任务也要等待清理链结束，才通知下一任务继续。
		if ctx.Err() != nil {
			return
		}
		err := a.runPlayback(ctx, req, generation)
		a.mu.Lock()
		defer a.mu.Unlock()
		if generation != a.playGeneration {
			return
		}
		a.playback.State = "stopped"
		if err != nil && !errors.Is(err, context.Canceled) && !errors.Is(err, media.ErrStopped) {
			a.playback.State = "error"
			_, code, message := explainError(err)
			a.playback.Error = message
			a.logger.Printf("播放结束 code=%s", code)
		}
	}()
}

func (a *app) runPlayback(ctx context.Context, req playRequest, generation uint64) error {
	resolveCtx, cancel := context.WithTimeout(ctx, 45*time.Second)
	defer cancel()
	detail, err := a.getDetail(resolveCtx, req.SeriesID)
	if err != nil {
		return err
	}
	var episode *model.Episode
	for i := range detail.Episodes {
		if detail.Episodes[i].ID == req.EpisodeID {
			episode = &detail.Episodes[i]
			break
		}
	}
	if episode == nil || !episode.Playable {
		return sdk.ErrContentUnavailable
	}
	pb, err := a.service.ResolvePlayback(resolveCtx, req.EpisodeID, sdk.PlaybackOptions{})
	if err != nil && !errors.Is(err, sdk.ErrDRMProtected) {
		return err
	}
	if pb == nil {
		return sdk.ErrAnonymousLimited
	}
	stream, err := pb.Select(req.Quality)
	if err != nil {
		return errors.New("所选清晰度暂未返回，请选择其他清晰度重试")
	}
	key := ""
	if pb.Protected {
		contentKey, err := sdk.ContentKey(pb)
		if err != nil {
			return err
		}
		key = contentKey.Hex
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	entry := history.Entry{SeriesID: req.SeriesID, Title: detail.Series.Title, Episode: episode.Number, Episodes: detail.Series.EpisodeCount}
	startSeconds := 0
	if previous, ok, err := a.history.Get(ctx, req.SeriesID); err == nil && ok && previous.Episode == episode.Number && !previous.Finished {
		startSeconds = int(previous.Position)
		entry.Position = previous.Position
	}
	if err := a.history.Record(ctx, entry); err != nil {
		a.logger.Print("播放前保存观看记录失败")
	}
	a.mu.Lock()
	if generation != a.playGeneration || ctx.Err() != nil {
		a.mu.Unlock()
		return context.Canceled
	}
	a.playback.State = "playing"
	a.playback.Title = detail.Series.Title
	a.mu.Unlock()
	result, playErr := a.player.Play(ctx, media.Request{
		URL: stream.URL, Audio: pb.AudioURL, Key: key, Player: a.selectedPlayer().Name,
		StartSeconds: startSeconds, DurationSeconds: pb.DurationSeconds,
	})
	entry.Position = result.Position
	entry.Finished = playErr == nil && result.Jump == 0
	// 停止播放会取消播放上下文，观看记录仍应独立完成保存。
	saveCtx, saveCancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer saveCancel()
	if err := a.history.Record(saveCtx, entry); err != nil {
		a.logger.Print("播放后保存观看记录失败")
	}
	if playErr != nil && !errors.Is(playErr, context.Canceled) && !errors.Is(playErr, media.ErrStopped) {
		return errors.New("播放器启动或播放失败，请检查网络、播放器安装和所选清晰度后重试")
	}
	return playErr
}

func (a *app) stopPlayback() {
	a.mu.Lock()
	defer a.mu.Unlock()
	a.playGeneration++
	if a.playCancel != nil {
		a.playCancel()
	}
	a.playback.State = "stopped"
	a.playback.Error = ""
}

func (a *app) selectedPlayer() toolInfo {
	for _, name := range []string{"mpv", "ffplay", "vlc"} {
		for _, tool := range a.tools {
			if tool.Name == name && tool.Available {
				return tool
			}
		}
	}
	return toolInfo{Name: "未检测到播放器"}
}

func detectTools() []toolInfo {
	executable, _ := os.Executable()
	base := filepath.Dir(executable)
	tools := make([]toolInfo, 0, 4)
	for _, name := range []string{"mpv", "ffplay", "vlc", "ffmpeg"} {
		tool := toolInfo{Name: name}
		candidates := []string{filepath.Join(base, "tools", name+".exe"), filepath.Join(base, name+".exe")}
		if name == "vlc" {
			for _, env := range []string{"ProgramFiles", "ProgramFiles(x86)"} {
				if dir := os.Getenv(env); dir != "" {
					candidates = append(candidates, filepath.Join(dir, "VideoLAN", "VLC", "vlc.exe"))
				}
			}
		}
		for _, path := range candidates {
			if info, err := os.Stat(path); err == nil && !info.IsDir() {
				tool.Path = path
				break
			}
		}
		if tool.Path == "" {
			tool.Path, _ = exec.LookPath(name)
		}
		tool.Available = tool.Path != ""
		tools = append(tools, tool)
	}
	return tools
}

func validID(id string) bool {
	if len(id) < 4 || len(id) > 25 {
		return false
	}
	for _, ch := range id {
		if ch < '0' || ch > '9' {
			return false
		}
	}
	return true
}

func (a *app) serviceError(w http.ResponseWriter, err error) {
	status, code, message := explainError(err)
	if a.logger != nil {
		a.logger.Printf("接口请求结束 code=%s", code)
	}
	writeError(w, status, code, message)
}

func explainError(err error) (int, string, string) {
	switch {
	case errors.Is(err, context.DeadlineExceeded):
		return http.StatusGatewayTimeout, "TIMEOUT", "请求超时，请检查网络后重试"
	case errors.Is(err, context.Canceled):
		return http.StatusRequestTimeout, "CANCELED", "操作已取消"
	case errors.Is(err, sdk.ErrRateLimited):
		return http.StatusTooManyRequests, "RATE_LIMITED", "请求较频繁，请稍后再试"
	case errors.Is(err, sdk.ErrAnonymousLimited), errors.Is(err, sdk.ErrDeviceRejected):
		return http.StatusBadGateway, "ANONYMOUS_LIMITED", "红果暂未向当前访客返回内容，请稍后重试"
	case errors.Is(err, sdk.ErrAuthenticationRequired):
		return http.StatusForbidden, "AUTH_REQUIRED", "此内容需要登录，请在红果官方客户端查看"
	case errors.Is(err, sdk.ErrChallengeRequired):
		return http.StatusForbidden, "CHALLENGE_REQUIRED", "服务端要求验证，请稍后重试或在红果官方客户端查看"
	case errors.Is(err, sdk.ErrSignatureRejected):
		return http.StatusBadGateway, "SIGNATURE_REJECTED", "服务端暂未接受当前请求，请稍后重试"
	case errors.Is(err, sdk.ErrContentUnavailable):
		return http.StatusNotFound, "CONTENT_UNAVAILABLE", "此剧集或分集暂不可用，请选择其他内容"
	case errors.Is(err, sdk.ErrDRMProtected):
		return http.StatusBadGateway, "PLAYBACK_UNAVAILABLE", "本次播放信息暂不可用，请稍后重试"
	case errors.Is(err, sdk.ErrTransport):
		return http.StatusBadGateway, "NETWORK_ERROR", "连接红果服务失败，请检查网络后重试"
	default:
		return http.StatusBadGateway, "REQUEST_FAILED", "操作未完成，请重试；如持续出现，请检查设置中的运行日志"
	}
}

func writeError(w http.ResponseWriter, status int, code, message string) {
	writeJSON(w, status, map[string]string{"error": message, "code": code})
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(value)
}
