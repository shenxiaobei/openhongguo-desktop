package nativebridge

import (
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

type suggestServiceStub struct {
	bridgeServiceStub
	suggest func(context.Context, string) ([]model.Suggestion, error)
}

func (s *suggestServiceStub) Suggest(ctx context.Context, query string) ([]model.Suggestion, error) {
	return s.suggest(ctx, query)
}

func TestSuggestForwardsPrefixAndUsesDisplayText(t *testing.T) {
	server := &Server{Service: &suggestServiceStub{suggest: func(_ context.Context, query string) ([]model.Suggestion, error) {
		if query != "全球" {
			t.Fatalf("query = %q", query)
		}
		return []model.Suggestion{{Text: " 全球死考 ", Keyword: "735001"}, {Text: "全球死考"}, {Text: ""}, {Text: "全球高考"}}, nil
	}}}
	result, err := server.Dispatch(context.Background(), bridgeRequest(t, "suggest", map[string]string{"query": " 全球 "}))
	if err != nil {
		t.Fatal(err)
	}
	words := result.([]string)
	if len(words) != 2 || words[0] != "全球死考" || words[1] != "全球高考" {
		t.Fatalf("suggestions = %#v", words)
	}
}

func TestSuggestBoundsAndCancellation(t *testing.T) {
	server := &Server{Service: &suggestServiceStub{suggest: func(ctx context.Context, _ string) ([]model.Suggestion, error) {
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		words := []model.Suggestion{{Text: strings.Repeat("剧", 101)}}
		for _, word := range []string{"一", "二", "三", "四", "五", "六", "七", "八", "九"} {
			words = append(words, model.Suggestion{Text: word})
		}
		return words, nil
	}}}
	for _, query := range []string{"", " ", strings.Repeat("剧", 101)} {
		_, err := server.Dispatch(context.Background(), bridgeRequest(t, "suggest", map[string]string{"query": query}))
		if !errors.Is(err, errInvalidInput) {
			t.Fatalf("query %q: %v", query, err)
		}
	}
	result, err := server.Dispatch(context.Background(), bridgeRequest(t, "suggest", map[string]string{"query": "剧"}))
	if err != nil || len(result.([]string)) != 8 {
		t.Fatalf("result = %#v, error = %v", result, err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := server.Dispatch(ctx, bridgeRequest(t, "suggest", map[string]string{"query": "剧"})); !errors.Is(err, context.Canceled) {
		t.Fatalf("canceled = %v", err)
	}
	unsupported := &Server{Service: &bridgeServiceStub{}}
	if _, err := unsupported.Dispatch(context.Background(), bridgeRequest(t, "suggest", map[string]string{"query": "剧"})); !errors.Is(err, sdk.ErrContentUnavailable) {
		t.Fatalf("unsupported = %v", err)
	}
}
