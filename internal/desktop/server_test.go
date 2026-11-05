package desktop

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"log"
	"net/http"
	"net/http/cookiejar"
	"net/http/httptest"
	"net/url"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/beilunyang/openhongguo/internal/history"
	"github.com/beilunyang/openhongguo/internal/media"
	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

const desktopTestOrigin = "http://127.0.0.1:43127"
const desktopTestToken = "desktop-session-for-tests"
const desktopTestCookie = "ohg_session_43127"

type desktopServiceStub struct {
	search  func(context.Context, string, sdk.SearchOptions) (*sdk.Page[model.SeriesSummary], error)
	detail  func(context.Context, []string) ([]model.SeriesDetail, error)
	resolve func(context.Context, string, sdk.PlaybackOptions) (*model.Playback, error)
}

func (s *desktopServiceStub) Discover(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error) {
	return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{}}, nil
}

func (s *desktopServiceStub) Search(ctx context.Context, query string, options sdk.SearchOptions) (*sdk.Page[model.SeriesSummary], error) {
	if s.search != nil {
		return s.search(ctx, query, options)
	}
	return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{}}, nil
}

func (s *desktopServiceStub) GetSeriesBatch(ctx context.Context, ids []string) ([]model.SeriesDetail, error) {
	if s.detail != nil {
		return s.detail(ctx, ids)
	}
	return []model.SeriesDetail{{
		Series: &model.Series{SeriesSummary: model.SeriesSummary{ID: "12345", Title: "测试短剧", EpisodeCount: 2}},
		Episodes: []model.Episode{
			{ID: "67890", SeriesID: "12345", Number: 1, Title: "第1集", Playable: true},
			{ID: "67891", SeriesID: "12345", Number: 2, Title: "第2集", Playable: true},
		},
	}}, nil
}

func (s *desktopServiceStub) ResolvePlayback(ctx context.Context, id string, options sdk.PlaybackOptions) (*model.Playback, error) {
	if s.resolve != nil {
		return s.resolve(ctx, id, options)
	}
	return &model.Playback{EpisodeID: id, Streams: []model.Stream{{URL: "https://example.invalid/video.mp4", Definition: "720p"}}}, nil
}

type desktopPlayerStub struct {
	play func(context.Context, media.Request) (media.Result, error)
}

func (p *desktopPlayerStub) Play(ctx context.Context, request media.Request) (media.Result, error) {
	if p.play != nil {
		return p.play(ctx, request)
	}
	return media.Result{}, nil
}

func newDesktopTestApp(t *testing.T, service Service) *app {
	t.Helper()
	dir := t.TempDir()
	ctx, cancel := context.WithCancel(context.Background())
	a := &app{
		ctx: ctx, service: service, player: &desktopPlayerStub{},
		history:   history.NewStore(filepath.Join(dir, "history.json")),
		configDir: dir, logFile: filepath.Join(dir, "desktop.log"),
		logger: log.New(io.Discard, "", 0), shutdown: cancel,
		lastPing: time.Now(), seriesCache: map[string]cachedDetail{},
		tools: []toolInfo{{Name: "ffplay", Path: "test-ffplay.exe", Available: true}},
	}
	t.Cleanup(func() {
		cancel()
		a.mu.Lock()
		done := a.playDone
		a.mu.Unlock()
		if done == nil {
			return
		}
		select {
		case <-done:
		case <-time.After(2 * time.Second):
			t.Error("测试播放器未及时释放")
		}
	})
	return a
}

func desktopRequest(handler http.Handler, method, path, body string, authenticated bool) *httptest.ResponseRecorder {
	request := httptest.NewRequest(method, desktopTestOrigin+path, strings.NewReader(body))
	if authenticated {
		request.AddCookie(&http.Cookie{Name: desktopTestCookie, Value: desktopTestToken})
	}
	if method != http.MethodGet {
		request.Header.Set("Origin", desktopTestOrigin)
		request.Header.Set("Content-Type", "application/json")
	}
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	return response
}

func TestDesktopSessionRejectsUnrelatedWebsites(t *testing.T) {
	a := newDesktopTestApp(t, &desktopServiceStub{})
	handler := a.handler(desktopTestOrigin, desktopTestToken)

	launch := desktopRequest(handler, http.MethodGet, "/?session="+desktopTestToken, "", false)
	if launch.Code != http.StatusSeeOther && launch.Code != http.StatusFound {
		t.Fatalf("启动链接没有清理令牌并跳转，状态=%d，正文=%s", launch.Code, launch.Body.String())
	}
	if launch.Header().Get("Location") != "/" {
		t.Fatalf("启动跳转位置=%q", launch.Header().Get("Location"))
	}
	var session *http.Cookie
	for _, cookie := range launch.Result().Cookies() {
		if cookie.Name == desktopTestCookie {
			session = cookie
		}
	}
	if session == nil || !session.HttpOnly || session.SameSite != http.SameSiteStrictMode || session.Path != "/" {
		t.Fatalf("会话Cookie属性不完整：%+v", session)
	}
	if response := desktopRequest(handler, http.MethodGet, "/api/status", "", true); response.Code != http.StatusOK {
		t.Fatalf("有效会话被拒绝：%d %s", response.Code, response.Body.String())
	}

	for _, path := range []string{"/", "/api/status", "/api/history"} {
		response := desktopRequest(handler, http.MethodGet, path, "", false)
		if response.Code < 400 || response.Code >= 500 {
			t.Errorf("无会话访问 %s 状态=%d", path, response.Code)
		}
	}
	for _, origin := range []string{"", "https://unrelated.example", desktopTestOrigin + ".unrelated.example"} {
		request := httptest.NewRequest(http.MethodPost, desktopTestOrigin+"/api/stop", nil)
		request.AddCookie(&http.Cookie{Name: desktopTestCookie, Value: desktopTestToken})
		request.Header.Set("Origin", origin)
		response := httptest.NewRecorder()
		handler.ServeHTTP(response, request)
		if response.Code < 400 || response.Code >= 500 {
			t.Errorf("外部Origin %q 状态=%d", origin, response.Code)
		}
	}
	request := httptest.NewRequest(http.MethodGet, desktopTestOrigin+"/api/status", nil)
	request.Host = "unrelated.example:43127"
	request.AddCookie(&http.Cookie{Name: desktopTestCookie, Value: desktopTestToken})
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	if response.Code < 400 || response.Code >= 500 {
		t.Fatalf("伪造Host状态=%d", response.Code)
	}
}

func TestDesktopSearchPreservesPaginationAndReportsServiceErrors(t *testing.T) {
	var calls atomic.Int32
	service := &desktopServiceStub{search: func(_ context.Context, query string, options sdk.SearchOptions) (*sdk.Page[model.SeriesSummary], error) {
		calls.Add(1)
		if query != "末世" || options.Cursor != "40" {
			t.Errorf("搜索参数丢失：query=%q cursor=%q", query, options.Cursor)
		}
		return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{{ID: "12345", Title: "测试短剧"}}, Cursor: "60", HasMore: true}, nil
	}}
	a := newDesktopTestApp(t, service)
	handler := a.handler(desktopTestOrigin, desktopTestToken)
	response := desktopRequest(handler, http.MethodGet, "/api/search?q="+url.QueryEscape("末世")+"&cursor=40", "", true)
	var page sdk.Page[model.SeriesSummary]
	if response.Code != http.StatusOK || json.Unmarshal(response.Body.Bytes(), &page) != nil {
		t.Fatalf("搜索失败：%d %s", response.Code, response.Body.String())
	}
	if len(page.Items) != 1 || page.Items[0].ID != "12345" || page.Cursor != "60" || !page.HasMore {
		t.Fatalf("分页结果丢失：%+v", page)
	}
	if calls.Load() != 1 {
		t.Fatalf("搜索调用次数=%d", calls.Load())
	}
	service.search = func(context.Context, string, sdk.SearchOptions) (*sdk.Page[model.SeriesSummary], error) {
		return nil, sdk.ErrAnonymousLimited
	}
	response = desktopRequest(handler, http.MethodGet, "/api/search?q=test", "", true)
	var failure map[string]any
	decodeErr := json.Unmarshal(response.Body.Bytes(), &failure)
	errorMessage, _ := failure["error"].(string)
	errorCode, _ := failure["code"].(string)
	if decodeErr != nil || response.Code < 400 || errorMessage == "" || errorCode == "" {
		t.Fatalf("服务失败没有可展示的错误：%d %s", response.Code, response.Body.String())
	}
}

func TestDesktopSeparateWindowsKeepIndependentCookies(t *testing.T) {
	jar, err := cookiejar.New(nil)
	if err != nil {
		t.Fatal(err)
	}
	sessions := []struct {
		origin  string
		token   string
		handler http.Handler
	}{
		{origin: desktopTestOrigin, token: desktopTestToken},
		{origin: "http://127.0.0.1:43128", token: "second-desktop-session"},
	}
	for i := range sessions {
		session := &sessions[i]
		a := newDesktopTestApp(t, &desktopServiceStub{})
		session.handler = a.handler(session.origin, session.token)
		request := httptest.NewRequest(http.MethodGet, session.origin+"/?session="+session.token, nil)
		response := httptest.NewRecorder()
		session.handler.ServeHTTP(response, request)
		if response.Code != http.StatusSeeOther {
			t.Fatalf("第%d个窗口启动失败：%d", i+1, response.Code)
		}
		originURL, err := url.Parse(session.origin)
		if err != nil {
			t.Fatal(err)
		}
		jar.SetCookies(originURL, response.Result().Cookies())
	}
	// 浏览器在同一主机的不同端口间共享Cookie，第二次启动不应破坏第一次会话。
	for _, session := range sessions {
		request := httptest.NewRequest(http.MethodGet, session.origin+"/api/status", nil)
		for _, cookie := range jar.Cookies(request.URL) {
			request.AddCookie(cookie)
		}
		response := httptest.NewRecorder()
		session.handler.ServeHTTP(response, request)
		if response.Code != http.StatusOK {
			t.Fatalf("另一窗口启动后会话失效：%s %d %s", session.origin, response.Code, response.Body.String())
		}
	}
}

func TestDesktopPlaybackOutlivesRequestAndStopsCleanly(t *testing.T) {
	a := newDesktopTestApp(t, &desktopServiceStub{})
	started := make(chan context.Context, 1)
	a.player = &desktopPlayerStub{play: func(ctx context.Context, _ media.Request) (media.Result, error) {
		started <- ctx
		<-ctx.Done()
		return media.Result{Position: 12.5}, ctx.Err()
	}}
	handler := a.handler(desktopTestOrigin, desktopTestToken)
	requestCtx, finishRequest := context.WithCancel(context.Background())
	request := httptest.NewRequest(http.MethodPost, desktopTestOrigin+"/api/play", strings.NewReader(`{"series_id":"12345","episode_id":"67890","quality":"best"}`)).WithContext(requestCtx)
	request.AddCookie(&http.Cookie{Name: desktopTestCookie, Value: desktopTestToken})
	request.Header.Set("Origin", desktopTestOrigin)
	request.Header.Set("Content-Type", "application/json")
	response := httptest.NewRecorder()
	handler.ServeHTTP(response, request)
	finishRequest()
	if response.Code < 200 || response.Code >= 300 {
		t.Fatalf("启动播放失败：%d %s", response.Code, response.Body.String())
	}
	var playerCtx context.Context
	select {
	case playerCtx = <-started:
	case <-time.After(2 * time.Second):
		t.Fatal("播放请求结束后没有启动播放器")
	}
	select {
	case <-playerCtx.Done():
		t.Fatal("HTTP请求结束导致播放器被取消")
	default:
	}
	response = desktopRequest(handler, http.MethodPost, "/api/stop", "", true)
	if response.Code != http.StatusOK {
		t.Fatalf("停止播放失败：%d %s", response.Code, response.Body.String())
	}
	select {
	case <-playerCtx.Done():
	case <-time.After(2 * time.Second):
		t.Fatal("停止操作没有取消播放器")
	}
	waitDesktopState(t, a, "stopped", "67890")
	a.mu.Lock()
	done := a.playDone
	a.mu.Unlock()
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("停止后播放器未完成保存")
	}
	entry, ok, err := a.history.Get(context.Background(), "12345")
	if err != nil || !ok || entry.Position != 12.5 || entry.Finished {
		t.Fatalf("停止后的观看进度丢失：entry=%+v ok=%v err=%v", entry, ok, err)
	}
}

func TestDesktopOldPlaybackCannotOverwriteNewSelection(t *testing.T) {
	service := &desktopServiceStub{resolve: func(_ context.Context, id string, _ sdk.PlaybackOptions) (*model.Playback, error) {
		return &model.Playback{EpisodeID: id, Streams: []model.Stream{{URL: "https://example.invalid/" + id + ".mp4"}}}, nil
	}}
	a := newDesktopTestApp(t, service)
	started := make(chan string, 2)
	releaseFirst := make(chan struct{})
	var releaseOnce sync.Once
	t.Cleanup(func() { releaseOnce.Do(func() { close(releaseFirst) }) })
	a.player = &desktopPlayerStub{play: func(ctx context.Context, request media.Request) (media.Result, error) {
		started <- request.URL
		if strings.Contains(request.URL, "67890") {
			// 模拟已收到取消、但还在释放资源的旧播放器。
			<-releaseFirst
			return media.Result{}, errors.New("旧播放器的延迟错误")
		}
		<-ctx.Done()
		return media.Result{}, ctx.Err()
	}}
	handler := a.handler(desktopTestOrigin, desktopTestToken)
	response := desktopRequest(handler, http.MethodPost, "/api/play", `{"series_id":"12345","episode_id":"67890"}`, true)
	if response.Code < 200 || response.Code >= 300 {
		t.Fatalf("首次播放失败：%d %s", response.Code, response.Body.String())
	}
	waitDesktopPlayer(t, started, "67890")
	a.mu.Lock()
	firstDone := a.playDone
	a.mu.Unlock()
	if firstDone == nil {
		t.Fatal("首次播放没有生命周期通知")
	}
	response = desktopRequest(handler, http.MethodPost, "/api/play", `{"series_id":"12345","episode_id":"67891"}`, true)
	if response.Code < 200 || response.Code >= 300 {
		t.Fatalf("切换选集失败：%d %s", response.Code, response.Body.String())
	}
	waitDesktopState(t, a, "loading", "67891")
	select {
	case <-started:
		t.Fatal("旧播放器清理结束之前启动了第二个播放器")
	default:
	}
	releaseOnce.Do(func() { close(releaseFirst) })
	select {
	case <-firstDone:
	case <-time.After(2 * time.Second):
		t.Fatal("旧播放器没有完成清理")
	}
	waitDesktopPlayer(t, started, "67891")
	waitDesktopState(t, a, "playing", "67891")
}

func TestDesktopRapidSelectionWaitsForWholeCleanupChain(t *testing.T) {
	service := &desktopServiceStub{resolve: func(_ context.Context, id string, _ sdk.PlaybackOptions) (*model.Playback, error) {
		return &model.Playback{EpisodeID: id, Streams: []model.Stream{{URL: "https://example.invalid/" + id + ".mp4"}}}, nil
	}}
	a := newDesktopTestApp(t, service)
	started := make(chan string, 3)
	releaseFirst := make(chan struct{})
	var releaseOnce sync.Once
	t.Cleanup(func() { releaseOnce.Do(func() { close(releaseFirst) }) })
	var playerCalls atomic.Int32
	a.player = &desktopPlayerStub{play: func(ctx context.Context, request media.Request) (media.Result, error) {
		call := playerCalls.Add(1)
		started <- request.URL
		if call == 1 {
			<-releaseFirst
			return media.Result{}, ctx.Err()
		}
		<-ctx.Done()
		return media.Result{}, ctx.Err()
	}}
	handler := a.handler(desktopTestOrigin, desktopTestToken)
	startEpisode := func(id string) {
		t.Helper()
		response := desktopRequest(handler, http.MethodPost, "/api/play", `{"series_id":"12345","episode_id":"`+id+`"}`, true)
		if response.Code != http.StatusAccepted {
			t.Fatalf("切换分集失败：%d %s", response.Code, response.Body.String())
		}
	}
	startEpisode("67890")
	waitDesktopPlayer(t, started, "67890")
	startEpisode("67891")
	a.mu.Lock()
	secondDone := a.playDone
	a.mu.Unlock()
	startEpisode("67890")
	waitDesktopState(t, a, "loading", "67890")
	select {
	case <-secondDone:
		t.Fatal("中间代在首个播放器释放前结束了清理链")
	case <-started:
		t.Fatal("首个播放器释放前启动了后续播放器")
	case <-time.After(100 * time.Millisecond):
	}
	releaseOnce.Do(func() { close(releaseFirst) })
	waitDesktopPlayer(t, started, "67890")
	select {
	case <-secondDone:
	case <-time.After(2 * time.Second):
		t.Fatal("中间代没有结束清理")
	}
	waitDesktopState(t, a, "playing", "67890")
	if playerCalls.Load() != 2 {
		t.Fatalf("过期分集也启动了播放器，总启动次数=%d", playerCalls.Load())
	}
}

func waitDesktopPlayer(t *testing.T, started <-chan string, episodeID string) {
	t.Helper()
	select {
	case rawURL := <-started:
		if !strings.Contains(rawURL, episodeID) {
			t.Fatalf("启动了错误分集：%s", rawURL)
		}
	case <-time.After(2 * time.Second):
		t.Fatalf("分集%s没有启动", episodeID)
	}
}

func waitDesktopState(t *testing.T, a *app, state, episodeID string) {
	t.Helper()
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		data, err := json.Marshal(a.snapshot())
		if err != nil {
			t.Fatal(err)
		}
		var status map[string]any
		if err := json.Unmarshal(data, &status); err != nil {
			t.Fatal(err)
		}
		if status["state"] == state && status["episode_id"] == episodeID {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("播放状态未达到%s / %s：%+v", state, episodeID, a.snapshot())
}

func TestDesktopRejectsInvalidInputBeforeSDKCalls(t *testing.T) {
	var calls atomic.Int32
	service := &desktopServiceStub{
		search: func(context.Context, string, sdk.SearchOptions) (*sdk.Page[model.SeriesSummary], error) {
			calls.Add(1)
			return nil, errors.New("意外调用搜索")
		},
		detail: func(context.Context, []string) ([]model.SeriesDetail, error) {
			calls.Add(1)
			return nil, errors.New("意外调用详情")
		},
		resolve: func(context.Context, string, sdk.PlaybackOptions) (*model.Playback, error) {
			calls.Add(1)
			return nil, errors.New("意外调用播放")
		},
	}
	a := newDesktopTestApp(t, service)
	handler := a.handler(desktopTestOrigin, desktopTestToken)
	tests := []struct{ method, path, body string }{
		{http.MethodGet, "/api/search?q=", ""},
		{http.MethodGet, "/api/series?id=../12345", ""},
		{http.MethodPost, "/api/play", "{"},
		{http.MethodPost, "/api/play", `{"series_id":"12345","episode_id":"67890"} {}`},
		{http.MethodPost, "/api/play", `{"series_id":"12345","episode_id":"invalid"}`},
		{http.MethodPost, "/api/play", `{"series_id":"12345","episode_id":"67890","quality":"invalid"}`},
		{http.MethodPost, "/api/play", strings.Repeat("x", 128*1024)},
	}
	for _, test := range tests {
		response := desktopRequest(handler, test.method, test.path, test.body, true)
		if response.Code < 400 || response.Code >= 500 {
			t.Errorf("输入未被正确拒绝：%s %s 状态=%d 正文=%s", test.method, test.path, response.Code, response.Body.String())
		}
	}
	if calls.Load() != 0 {
		t.Fatalf("无效请求访问了SDK：%d次", calls.Load())
	}
}
