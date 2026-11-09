// Package nativebridge 通过标准输入输出连接原生窗口与 Go SDK，不监听网络端口。
package nativebridge

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"math"
	"strings"
	"sync"
	"time"

	"github.com/beilunyang/openhongguo/internal/history"
	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

const Version = "1.0-native.1"

type Service interface {
	Discover(context.Context, sdk.DiscoverOptions) (*sdk.Page[model.SeriesSummary], error)
	Search(context.Context, string, sdk.SearchOptions) (*sdk.Page[model.SeriesSummary], error)
	GetSeriesBatch(context.Context, []string) ([]model.SeriesDetail, error)
	SeriesRecommend(context.Context, string) (*model.Recommend, error)
	ResolvePlayback(context.Context, string, sdk.PlaybackOptions) (*model.Playback, error)
}

type shortRankService interface {
	ShortRank(context.Context, sdk.ShortRankQuery) (*model.ShortRanking, error)
}

type searchSuggestionService interface {
	Suggest(context.Context, string) ([]model.Suggestion, error)
}

type shortFilterService interface {
	ShortFilterPanel(context.Context, string) ([]model.FilterRow, error)
	ShortFilter(context.Context, sdk.ShortFilterQuery) (*model.ShortFilterPage, error)
}

type Request struct {
	ID     string          `json:"id"`
	Method string          `json:"method"`
	Args   json.RawMessage `json:"args"`
}

type Failure struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

type response struct {
	ID     string   `json:"id"`
	Result any      `json:"result,omitempty"`
	Error  *Failure `json:"error,omitempty"`
}

type cachedDetail struct {
	Detail model.SeriesDetail
	At     time.Time
}

type cachedPlayback struct {
	Playback *model.Playback
	At       time.Time
}

type playbackQuality struct {
	Definition string `json:"definition"`
	Width      int    `json:"width"`
	Height     int    `json:"height"`
	Bitrate    int64  `json:"bitrate"`
}

type engagement struct {
	SeriesID          string `json:"series_id"`
	EpisodeID         string `json:"episode_id"`
	PlayCount         *int64 `json:"play_count"`
	FollowCount       *int64 `json:"follow_count"`
	SeriesLikeCount   *int64 `json:"series_like_count"`
	EpisodeLikeCount  *int64 `json:"episode_like_count"`
	CommentCount      *int64 `json:"comment_count"`
	FavoriteCount     *int64 `json:"favorite_count"`
	CommentsAvailable bool   `json:"comments_available"`
	DanmakuAvailable  bool   `json:"danmaku_available"`
	Note              string `json:"note"`
}

type Server struct {
	Service   Service
	History   *history.Store
	ConfigDir string
	Logger    *log.Logger
	mu        sync.Mutex
	details   map[string]cachedDetail
	playbacks map[string]cachedPlayback
}

// Serve 在窗口关闭输入流时取消所有任务，避免留下隐藏的辅助进程。
func (s *Server) Serve(parent context.Context, input io.Reader, output io.Writer) error {
	ctx, cancel := context.WithCancel(parent)
	defer cancel()
	scanner := bufio.NewScanner(input)
	scanner.Buffer(make([]byte, 8192), 1<<20)
	var outputMu sync.Mutex
	encoder := json.NewEncoder(output)
	write := func(value response) {
		outputMu.Lock()
		defer outputMu.Unlock()
		_ = encoder.Encode(value)
	}
	var workers sync.WaitGroup
	var callsMu sync.Mutex
	calls := make(map[string]context.CancelFunc)
	limit := make(chan struct{}, 8)
	for scanner.Scan() {
		var request Request
		if err := json.Unmarshal(scanner.Bytes(), &request); err != nil || request.ID == "" || len(request.ID) > 100 {
			write(response{ID: request.ID, Error: &Failure{Code: "INVALID_REQUEST", Message: "请求格式不正确"}})
			continue
		}
		if request.Method == "cancel" {
			var args struct {
				RequestID string `json:"request_id"`
			}
			if json.Unmarshal(request.Args, &args) == nil {
				callsMu.Lock()
				if stop := calls[args.RequestID]; stop != nil {
					stop()
				}
				callsMu.Unlock()
			}
			continue
		}
		callCtx, stop := context.WithTimeout(ctx, 60*time.Second)
		callsMu.Lock()
		_, duplicate := calls[request.ID]
		if !duplicate {
			calls[request.ID] = stop
		}
		callsMu.Unlock()
		if duplicate {
			stop()
			write(response{ID: request.ID, Error: &Failure{Code: "DUPLICATE_REQUEST", Message: "请求编号重复"}})
			continue
		}
		workers.Add(1)
		go func(request Request, callCtx context.Context, stop context.CancelFunc) {
			defer workers.Done()
			defer stop()
			defer func() {
				callsMu.Lock()
				delete(calls, request.ID)
				callsMu.Unlock()
			}()
			select {
			case limit <- struct{}{}:
				defer func() { <-limit }()
			case <-callCtx.Done():
				write(response{ID: request.ID, Error: explainError(callCtx.Err())})
				return
			}
			result, err := s.Dispatch(callCtx, request)
			if err != nil {
				failure := explainError(err)
				if s.Logger != nil {
					s.Logger.Printf("请求结束 method=%s code=%s", request.Method, failure.Code)
				}
				write(response{ID: request.ID, Error: failure})
				return
			}
			write(response{ID: request.ID, Result: result})
		}(request, callCtx, stop)
	}
	cancel()
	workers.Wait()
	return scanner.Err()
}

func (s *Server) Dispatch(ctx context.Context, request Request) (any, error) {
	var args struct {
		ID         string              `json:"id"`
		Query      string              `json:"query"`
		Tab        string              `json:"tab"`
		IDs        []string            `json:"ids"`
		Channel    string              `json:"channel"`
		Content    string              `json:"content"`
		Board      string              `json:"board"`
		Genre      string              `json:"genre"`
		Selections map[string][]string `json:"selections"`
		Cursor     string              `json:"cursor"`
		SeriesID   string              `json:"series_id"`
		EpisodeID  string              `json:"episode_id"`
		Quality    string              `json:"quality"`
		DurationMS int64               `json:"duration_ms"`
	}
	if len(request.Args) > 0 && string(request.Args) != "null" {
		if err := json.Unmarshal(request.Args, &args); err != nil {
			return nil, errInvalidInput
		}
	}
	if len(args.Cursor) > 16384 || len(args.Channel) > 100 {
		return nil, errInvalidInput
	}
	switch request.Method {
	case "status":
		return map[string]any{"version": Version, "config_dir": s.ConfigDir, "native": true, "local_server": false}, nil
	case "discover":
		// 首屏只取当前页，后续页和封面由界面分别加载，不串行等待整批详情。
		return s.catalog(ctx, "discover", args.Channel, args.Cursor, "")
	case "rank":
		if len(args.Content) > 100 || len(args.Board) > 100 {
			return nil, errInvalidInput
		}
		service, ok := s.Service.(shortRankService)
		if !ok {
			return nil, sdk.ErrContentUnavailable
		}
		// 保留服务端目录、页面顺序及推广标记，不把推荐列表重新排成榜单。
		return service.ShortRank(ctx, sdk.ShortRankQuery{Content: args.Content, Board: args.Board, Cursor: args.Cursor})
	case "filter_options":
		if !validFilterGenre(args.Genre) {
			return nil, errInvalidInput
		}
		service, ok := s.Service.(shortFilterService)
		if !ok {
			return nil, sdk.ErrContentUnavailable
		}
		rows, err := service.ShortFilterPanel(ctx, args.Genre)
		if err != nil {
			return nil, err
		}
		if rows == nil {
			rows = []model.FilterRow{}
		}
		return map[string]any{"rows": rows}, nil
	case "filter":
		if !validFilterSelections(args.Selections) {
			return nil, errInvalidInput
		}
		service, ok := s.Service.(shortFilterService)
		if !ok {
			return nil, sdk.ErrContentUnavailable
		}
		page, err := service.ShortFilter(ctx, sdk.ShortFilterQuery{Selections: args.Selections, Cursor: args.Cursor})
		if err != nil {
			return nil, err
		}
		if page == nil {
			page = &model.ShortFilterPage{}
		}
		if page.Items == nil {
			page.Items = []model.SeriesSummary{}
		}
		// 筛选结果保留服务端的顺序与续页条件，补图不阻塞目录显示。
		s.applyCached(page.Items)
		return page, nil
	case "comments", "danmaku":
		return s.discussion(ctx, request.Method, args.SeriesID, args.EpisodeID, args.Cursor, args.DurationMS)
	case "engagement":
		if !validID(args.SeriesID) || (args.EpisodeID != "" && !validID(args.EpisodeID)) {
			return nil, errInvalidInput
		}
		detail, err := s.detail(ctx, args.SeriesID)
		if err != nil {
			return nil, err
		}
		result := engagement{
			SeriesID: args.SeriesID, EpisodeID: args.EpisodeID,
			PlayCount: knownCount(detail.Series.PlayCount), FollowCount: knownCount(detail.Series.FollowCount),
			SeriesLikeCount: knownCount(detail.Series.DiggCount),
			Note:            "追剧数与收藏不同；互动正文以单独加载结果为准，未确认的计数显示 —。",
		}
		if args.EpisodeID == "" {
			return result, nil
		}
		for _, episode := range detail.Episodes {
			if episode.ID == args.EpisodeID {
				result.EpisodeLikeCount = knownCount(episode.DiggCount)
				result.CommentCount = knownCount(episode.CommentCount)
				// 旗标只表示接口支持，正文是否返回由独立请求决定。
				_, result.CommentsAvailable = s.Service.(episodeCommentsService)
				_, result.DanmakuAvailable = s.Service.(danmakuService)
				return result, nil
			}
		}
		return nil, sdk.ErrContentUnavailable
	case "suggest":
		query := strings.TrimSpace(args.Query)
		if query == "" || len([]rune(query)) > 100 {
			return nil, errInvalidInput
		}
		service, ok := s.Service.(searchSuggestionService)
		if !ok {
			return nil, sdk.ErrContentUnavailable
		}
		items, err := service.Suggest(ctx, query)
		if err != nil {
			return nil, err
		}
		// 联想只提交显示词；Keyword 可能是直达内容 ID，不作为搜索文本。
		words := make([]string, 0, 8)
		seen := make(map[string]bool)
		for _, item := range items {
			word := strings.TrimSpace(item.Text)
			if word == "" || len([]rune(word)) > 100 || seen[word] {
				continue
			}
			seen[word] = true
			words = append(words, word)
			if len(words) == 8 {
				break
			}
		}
		return words, nil
	case "search":
		query := strings.TrimSpace(args.Query)
		if query == "" || len([]rune(query)) > 100 {
			return nil, errInvalidInput
		}
		if args.Tab == "" {
			args.Tab = "剧集"
		}
		switch args.Tab {
		case "综合", "剧集", "视频":
		default:
			return nil, errInvalidInput
		}
		page, err := s.Service.Search(ctx, query, sdk.SearchOptions{Cursor: args.Cursor, PageSize: 20, Tab: args.Tab})
		if err != nil {
			return nil, err
		}
		page = normalizePage(page)
		s.applyCached(page.Items)
		return page, nil
	case "hydrate":
		if len(args.IDs) > 100 {
			return nil, errInvalidInput
		}
		items := make([]model.SeriesSummary, 0, len(args.IDs))
		seen := make(map[string]bool)
		for _, id := range args.IDs {
			if !validID(id) {
				return nil, errInvalidInput
			}
			if !seen[id] {
				items = append(items, model.SeriesSummary{ID: id})
				seen[id] = true
			}
		}
		s.enrich(ctx, items)
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		return map[string]any{"items": items}, nil
	case "series":
		if !validID(args.ID) {
			return nil, errInvalidInput
		}
		detail, err := s.detail(ctx, args.ID)
		if err != nil {
			return nil, err
		}
		// 只调整原生窗口的展示副本，缓存和 SDK 的原始封面保留不变。
		series := *detail.Series
		fillSummary(&series.SeriesSummary, detail)
		detail.Series = &series
		return detail, nil
	case "seasons":
		if !validID(args.ID) {
			return nil, errInvalidInput
		}
		return s.seasons(ctx, args.ID)
	case "recommend":
		return s.catalog(ctx, "recommend", args.Channel, args.Cursor, args.SeriesID)
	case "playback":
		if !validID(args.EpisodeID) {
			return nil, errInvalidInput
		}
		if len(args.Quality) > 100 {
			return nil, errInvalidInput
		}
		pb, err := s.playback(ctx, args.EpisodeID)
		if err != nil {
			return nil, err
		}
		stream, err := pb.Select(args.Quality)
		if err != nil {
			return nil, errQualityUnavailable
		}
		key := ""
		if pb.Protected {
			contentKey, err := sdk.ContentKey(pb)
			if err != nil {
				return nil, err
			}
			key = contentKey.Hex
		}
		// 播放参数只通过父子进程的私有管道传递，不输出到日志或界面。
		definition := streamSelector(stream)
		return map[string]any{"url": stream.URL, "audio_url": pb.AudioURL, "key": key, "duration_seconds": pb.DurationSeconds, "duration_ms": pb.DurationMS, "expires_at": pb.ExpiresAt, "definition": definition, "width": stream.Width, "height": stream.Height, "bitrate": stream.Bitrate, "available_qualities": availableQualities(pb)}, nil
	case "history":
		return s.History.List(ctx)
	case "history_record":
		var entry history.Entry
		if err := json.Unmarshal(request.Args, &entry); err != nil {
			return nil, errInvalidInput
		}
		if !validID(entry.SeriesID) || entry.Episode < 1 || entry.Episodes < 0 || (entry.Episodes > 0 && entry.Episode > entry.Episodes) || len(entry.Title) > 1000 || entry.Position < 0 || math.IsInf(entry.Position, 0) || math.IsNaN(entry.Position) {
			return nil, errInvalidInput
		}
		entry.UpdatedAt = time.Now().UTC()
		if err := s.History.Record(ctx, entry); err != nil {
			return nil, err
		}
		return map[string]bool{"ok": true}, nil
	default:
		return nil, errInvalidInput
	}
}

func (s *Server) discover(ctx context.Context, channel, cursor string, minimum int) (*sdk.Page[model.SeriesSummary], error) {
	if channel == "" {
		channel = "推荐"
	}
	result := &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{}}
	next := cursor
	for i := 0; i < 3; i++ {
		page, err := s.Service.Discover(ctx, sdk.DiscoverOptions{Channel: channel, Cursor: next})
		if err != nil {
			if len(result.Items) == 0 {
				return nil, err
			}
			break
		}
		page = normalizePage(page)
		result.Items = unique(append(result.Items, page.Items...), "")
		result.Cursor = page.Cursor
		result.HasMore = page.HasMore && page.Cursor != ""
		if page.Cursor == next {
			result.HasMore = false
		}
		if len(result.Items) >= minimum || !result.HasMore {
			break
		}
		next = page.Cursor
	}
	s.applyCached(result.Items)
	return result, nil
}

// applyCached 只读取已有详情，不让目录请求等待网络补图。
func (s *Server) applyCached(items []model.SeriesSummary) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for i := range items {
		cached, ok := s.details[items[i].ID]
		if ok && time.Since(cached.At) < 5*time.Minute {
			fillSummary(&items[i], cached.Detail)
		}
	}
}

func (s *Server) playback(ctx context.Context, id string) (*model.Playback, error) {
	s.mu.Lock()
	cached, ok := s.playbacks[id]
	s.mu.Unlock()
	if ok && time.Since(cached.At) < 2*time.Minute && (cached.Playback.ExpiresAt == 0 || cached.Playback.ExpiresAt > time.Now().Unix()+30) {
		return cached.Playback, nil
	}
	pb, err := s.Service.ResolvePlayback(ctx, id, sdk.PlaybackOptions{})
	if err != nil && !errors.Is(err, sdk.ErrDRMProtected) {
		return nil, err
	}
	if pb == nil {
		return nil, sdk.ErrAnonymousLimited
	}
	if pb.Protected {
		if _, err := sdk.ContentKey(pb); err != nil {
			// 损坏的密钥材料不进入缓存，重试时重新请求播放模型。
			return nil, err
		}
	}
	// 同一集切换画质复用尚未过期的模型，媒体参数只留在内存中。
	s.mu.Lock()
	if s.playbacks == nil || len(s.playbacks) >= 10 {
		s.playbacks = make(map[string]cachedPlayback)
	}
	s.playbacks[id] = cachedPlayback{Playback: pb, At: time.Now()}
	s.mu.Unlock()
	return pb, nil
}

func streamSelector(stream model.Stream) string {
	if stream.Definition != "" {
		return stream.Definition
	}
	if stream.Quality != "" {
		return stream.Quality
	}
	return stream.Codec
}

func availableQualities(pb *model.Playback) []playbackQuality {
	qualities := make([]playbackQuality, 0, len(pb.Streams))
	seen := make(map[string]bool)
	for _, candidate := range pb.Streams {
		selector := streamSelector(candidate)
		if selector == "" || seen[strings.ToLower(selector)] || strings.TrimSpace(candidate.URL) == "" {
			continue
		}
		stream, err := pb.Select(selector)
		if err != nil {
			continue
		}
		seen[strings.ToLower(selector)] = true
		qualities = append(qualities, playbackQuality{Definition: selector, Width: stream.Width, Height: stream.Height, Bitrate: stream.Bitrate})
	}
	return qualities
}

func normalizePage(page *sdk.Page[model.SeriesSummary]) *sdk.Page[model.SeriesSummary] {
	if page == nil {
		return &sdk.Page[model.SeriesSummary]{Items: []model.SeriesSummary{}}
	}
	if page.Items == nil {
		page.Items = []model.SeriesSummary{}
	}
	return page
}

func unique(items []model.SeriesSummary, omit string) []model.SeriesSummary {
	seen := make(map[string]bool)
	result := make([]model.SeriesSummary, 0, len(items))
	for _, item := range items {
		if !validID(item.ID) || item.ID == omit || seen[item.ID] {
			continue
		}
		seen[item.ID] = true
		result = append(result, item)
	}
	return result
}

func (s *Server) detail(ctx context.Context, id string) (model.SeriesDetail, error) {
	s.mu.Lock()
	cached, ok := s.details[id]
	s.mu.Unlock()
	if ok && time.Since(cached.At) < 5*time.Minute {
		return cached.Detail, nil
	}
	details, err := s.Service.GetSeriesBatch(ctx, []string{id})
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
		s.cache(detail)
		return detail, nil
	}
	return model.SeriesDetail{}, sdk.ErrContentUnavailable
}

func (s *Server) cache(detail model.SeriesDetail) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.details == nil || len(s.details) > 200 {
		s.details = make(map[string]cachedDetail)
	}
	s.details[detail.Series.ID] = cachedDetail{Detail: detail, At: time.Now()}
}

// enrich 为未携带图片的目录批量补充详情，单批不超过服务端支持的十部。
func (s *Server) enrich(ctx context.Context, items []model.SeriesSummary) {
	var pending []string
	for i := range items {
		s.mu.Lock()
		cached, ok := s.details[items[i].ID]
		s.mu.Unlock()
		if ok && time.Since(cached.At) < 5*time.Minute {
			fillSummary(&items[i], cached.Detail)
			continue
		}
		if items[i].CoverURL == "" {
			pending = append(pending, items[i].ID)
		}
	}
	for start := 0; start < len(pending); start += 10 {
		if ctx.Err() != nil {
			return
		}
		details, err := s.Service.GetSeriesBatch(ctx, pending[start:min(start+10, len(pending))])
		if err != nil {
			continue
		}
		for _, detail := range details {
			if detail.Series == nil {
				continue
			}
			s.cache(detail)
			for i := range items {
				if items[i].ID == detail.Series.ID {
					fillSummary(&items[i], detail)
				}
			}
		}
	}
}

func fillSummary(item *model.SeriesSummary, detail model.SeriesDetail) {
	if detail.Series == nil {
		return
	}
	// 原生窗口已内置 HEIC 解码，展示真正的系列海报，不再用分集截图冒充。
	if detail.Series.CoverURL != "" {
		item.CoverURL = detail.Series.CoverURL
		item.CoverExpiresAt = detail.Series.CoverExpiresAt
	}
	if item.Title == "" {
		item.Title = detail.Series.Title
	}
	if item.EpisodeCount == 0 {
		item.EpisodeCount = detail.Series.EpisodeCount
	}
	if item.Description == "" {
		item.Description = detail.Series.Description
	}
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

func validFilterGenre(genre string) bool {
	switch genre {
	case "", "short_play", "comic_series", "ai_series":
		return true
	default:
		return false
	}
}

func validFilterSelections(selections map[string][]string) bool {
	if len(selections) > 8 {
		return false
	}
	for key, values := range selections {
		switch key {
		case "category_dim_epoch", "online_time", "gender", "category_dim_role", "genre", "creation_status", "sort", "category_dim_theme":
		default:
			return false
		}
		if len(values) > 20 {
			return false
		}
		for _, value := range values {
			if strings.TrimSpace(value) == "" || len(value) > 200 {
				return false
			}
		}
		if key == "genre" && (len(values) > 1 || (len(values) == 1 && !validFilterGenre(values[0]))) {
			return false
		}
	}
	return true
}

func knownCount(value int64) *int64 {
	// SDK 把缺失与真实零都解析成 0；只展示已确认的正数，避免伪造统计。
	if value <= 0 {
		return nil
	}
	return &value
}

var errInvalidInput = errors.New("输入参数不正确")
var errQualityUnavailable = errors.New("所选画质暂未返回，请改选最佳画质")
var errRecommendationEmpty = errors.New("推荐列表暂未返回内容")

func explainError(err error) *Failure {
	switch {
	case errors.Is(err, context.Canceled):
		return &Failure{Code: "CANCELED", Message: "操作已取消"}
	case errors.Is(err, context.DeadlineExceeded):
		return &Failure{Code: "TIMEOUT", Message: "请求超时，请稍后重试"}
	case errors.Is(err, errInvalidInput):
		return &Failure{Code: "INVALID_INPUT", Message: "输入参数不正确"}
	case errors.Is(err, errQualityUnavailable):
		return &Failure{Code: "QUALITY_UNAVAILABLE", Message: errQualityUnavailable.Error()}
	case errors.Is(err, sdk.ErrDeviceRejected):
		return &Failure{Code: "DEVICE_REJECTED", Message: "当前设备请求被服务端拒绝，请稍后重试"}
	case errors.Is(err, errRecommendationEmpty):
		return &Failure{Code: "ANONYMOUS_LIMITED", Message: "推荐列表与热门目录暂未返回内容，请稍后重试"}
	case errors.Is(err, sdk.ErrAnonymousLimited):
		return &Failure{Code: "ANONYMOUS_LIMITED", Message: "红果暂未返回此内容，请稍后重试"}
	case errors.Is(err, sdk.ErrRateLimited):
		return &Failure{Code: "RATE_LIMITED", Message: "请求较频繁，请稍后重试"}
	case errors.Is(err, sdk.ErrContentUnavailable):
		return &Failure{Code: "CONTENT_UNAVAILABLE", Message: "此内容暂不可用，请选择其他剧集"}
	case errors.Is(err, sdk.ErrAuthenticationRequired), errors.Is(err, sdk.ErrChallengeRequired):
		return &Failure{Code: "VERIFICATION_REQUIRED", Message: "此内容要求账号或验证，请在官方客户端查看"}
	case errors.Is(err, sdk.ErrTransport):
		return &Failure{Code: "NETWORK_ERROR", Message: "连接服务失败，请检查网络后重试"}
	case errors.Is(err, sdk.ErrDRMProtected):
		return &Failure{Code: "PLAYBACK_UNAVAILABLE", Message: "播放信息暂不可用，请稍后重试"}
	default:
		return &Failure{Code: "REQUEST_FAILED", Message: "操作未完成，请稍后重试"}
	}
}

// EncodeRequest 供本地诊断构造同一协议的请求，避免界面与测试各自拼接格式。
func EncodeRequest(id, method string, args any) ([]byte, error) {
	raw, err := json.Marshal(args)
	if err != nil {
		return nil, fmt.Errorf("编码参数：%w", err)
	}
	return json.Marshal(Request{ID: id, Method: method, Args: raw})
}
