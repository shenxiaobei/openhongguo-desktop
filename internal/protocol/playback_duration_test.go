package protocol

import (
	"encoding/json"
	"fmt"
	"testing"
)

func TestParsePlaybackPreservesMillisecondDurationAndLegacySeconds(t *testing.T) {
	for _, tt := range []struct {
		duration    string
		wantMS      int64
		wantSeconds int
	}{
		{duration: "88.4", wantMS: 88400, wantSeconds: 88},
		{duration: "88.9", wantMS: 88900, wantSeconds: 89},
		{duration: "88.123", wantMS: 88123, wantSeconds: 88},
		{duration: "42", wantMS: 42000, wantSeconds: 42},
	} {
		t.Run(tt.duration, func(t *testing.T) {
			raw := []byte(fmt.Sprintf(`{"code":0,"data":{"video_model":{"video_duration":%s,"video_list":[{"main_url":"https://media.invalid/video.mp4"}]}}}`, tt.duration))
			playback, err := ParsePlayback(raw, "7100000000000301")
			if err != nil {
				t.Fatal(err)
			}
			if playback.DurationMS != tt.wantMS || playback.DurationSeconds != tt.wantSeconds {
				t.Fatalf("时长解析丢失精度或改变旧秒数：milliseconds=%d seconds=%d", playback.DurationMS, playback.DurationSeconds)
			}
			encoded, err := json.Marshal(playback)
			if err != nil {
				t.Fatal(err)
			}
			var fields struct {
				Milliseconds int64 `json:"duration_ms"`
				Seconds      int   `json:"duration_seconds"`
			}
			if err := json.Unmarshal(encoded, &fields); err != nil || fields.Milliseconds != tt.wantMS || fields.Seconds != tt.wantSeconds {
				t.Fatalf("时长 JSON 契约不一致：%s err=%v", encoded, err)
			}
		})
	}
}
