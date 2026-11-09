package nativebridge

import (
	"context"
	"errors"
	"testing"

	"github.com/beilunyang/openhongguo/sdk"
	"github.com/beilunyang/openhongguo/sdk/model"
)

func TestPlaybackRetryRefreshesInvalidProtectedModel(t *testing.T) {
	calls := 0
	server := &Server{Service: &bridgeServiceStub{playback: func(context.Context, string, sdk.PlaybackOptions) (*model.Playback, error) {
		calls++
		stream := model.Stream{Definition: "1080p", URL: "https://media.invalid/video.mp4"}
		if calls == 1 {
			return &model.Playback{Protected: true, KeyMaterial: "invalid", Streams: []model.Stream{stream}}, sdk.ErrDRMProtected
		}
		return &model.Playback{Streams: []model.Stream{stream}}, nil
	}}}
	request := bridgeRequest(t, "playback", map[string]string{"episode_id": "2001", "quality": "best"})

	if _, err := server.Dispatch(context.Background(), request); !errors.Is(err, sdk.ErrDRMProtected) {
		t.Fatalf("无效密钥模型应返回播放错误，得到 %v", err)
	}
	if len(server.playbacks) != 0 {
		t.Fatal("无效密钥模型进入了播放缓存")
	}
	for i := 0; i < 2; i++ {
		if _, err := server.Dispatch(context.Background(), request); err != nil {
			t.Fatalf("第 %d 次重试失败：%v", i+1, err)
		}
	}
	if calls != 2 {
		t.Fatalf("重试没有刷新模型，或有效模型没有缓存：调用 %d 次", calls)
	}
}

func TestPlaybackCachesValidProtectedModelAcrossQualities(t *testing.T) {
	const wrappedKey = "nLwSxG6JI85zhT//c7AK4HauO+lzrg7TdZwO0XWrP+J0pDypqQ=="
	calls := 0
	server := &Server{Service: &bridgeServiceStub{playback: func(context.Context, string, sdk.PlaybackOptions) (*model.Playback, error) {
		calls++
		return &model.Playback{Protected: true, KeyMaterial: wrappedKey, Streams: []model.Stream{
			{Definition: "720p", Width: 1280, Height: 720, URL: "https://media.invalid/720.mp4"},
			{Definition: "1080p", Width: 1920, Height: 1080, URL: "https://media.invalid/1080.mp4"},
		}}, sdk.ErrDRMProtected
	}}}

	for _, quality := range []string{"best", "720p"} {
		request := bridgeRequest(t, "playback", map[string]string{"episode_id": "2001", "quality": quality})
		result, err := server.Dispatch(context.Background(), request)
		if err != nil {
			t.Fatalf("%s 播放失败：%v", quality, err)
		}
		if result.(map[string]any)["key"] == "" {
			t.Fatalf("%s 丢失播放密钥", quality)
		}
	}
	if calls != 1 {
		t.Fatalf("切换画质重复请求了有效模型：调用 %d 次", calls)
	}
}
