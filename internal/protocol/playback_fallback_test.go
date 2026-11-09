package protocol

import (
	"fmt"
	"strings"
	"testing"
)

const fallbackDynamicModel = `{
	"dynamic_video_list": [
		{"main_url":"https://media.invalid/dynamic-720.mp4","spade_a":"fixture-dynamic-key","video_meta":{"definition":"720p","vtype":"dash","vwidth":1280,"vheight":720,"bitrate":2000000}},
		{"main_url":"https://media.invalid/dynamic-1080.mp4","spade_a":"fixture-dynamic-key","video_meta":{"definition":"1080p","vtype":"dash","vwidth":1920,"vheight":1080,"bitrate":4000000}}
	],
	"dynamic_audio_list": [{"main_url":"https://media.invalid/dynamic-audio.mp4"}]
}`

func TestParsePlaybackFallsBackWithoutUsableOrdinaryURL(t *testing.T) {
	tests := []struct {
		name      string
		listField string
	}{
		{name: "missing"},
		{name: "null", listField: `"video_list":null,`},
		{name: "empty_array", listField: `"video_list":[],`},
		{name: "empty_object", listField: `"video_list":{},`},
		{name: "spaced_empty_array", listField: `"video_list":[ ],`},
		{name: "spaced_empty_object", listField: `"video_list":{ },`},
		{name: "array_without_urls", listField: `"video_list":[{"definition":"2160p","spade_a":"fixture-unused-key"},{"main_url":"  "}],`},
		{name: "object_without_urls", listField: `"video_list":{"2160p":{"main_url":"\t","spade_a":"fixture-unused-key"}},`},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			raw := []byte(fmt.Sprintf(`{"code":0,"data":{"video_model":{%s"dynamic_video":%s}}}`, tt.listField, fallbackDynamicModel))
			playback, err := ParsePlayback(raw, "7100000000000301")
			if err != nil {
				t.Fatal(err)
			}
			if len(playback.Streams) != 2 {
				t.Fatalf("streams = %d, want both dynamic qualities", len(playback.Streams))
			}
			best, err := playback.Select("best")
			if err != nil {
				t.Fatal(err)
			}
			if best.URL != "https://media.invalid/dynamic-1080.mp4" || best.Width != 1920 || best.Height != 1080 || !best.Dash {
				t.Fatalf("best = %+v", best)
			}
			if playback.AudioURL != "https://media.invalid/dynamic-audio.mp4" {
				t.Fatalf("audio URL = %q", playback.AudioURL)
			}
			if !playback.Protected || playback.KeyMaterial != "fixture-dynamic-key" {
				t.Fatal("dynamic protection context was not retained")
			}
		})
	}
}

func TestParsePlaybackSelectsCompleteHigherQualityGroup(t *testing.T) {
	tests := []struct {
		name        string
		definition  string
		width       int
		height      int
		bitrate     int64
		wantDynamic bool
	}{
		{name: "dynamic_higher_resolution", definition: "360p", width: 640, height: 360, bitrate: 400000, wantDynamic: true},
		{name: "ordinary_higher_resolution", definition: "2160p", width: 3840, height: 2160, bitrate: 2000000},
		{name: "dynamic_higher_bitrate_at_same_resolution", definition: "1080p", width: 1920, height: 1080, bitrate: 1000000, wantDynamic: true},
		{name: "ordinary_higher_bitrate_at_same_resolution", definition: "1080p", width: 1920, height: 1080, bitrate: 5000000},
		{name: "same_resolution_and_bitrate_keeps_ordinary", definition: "1080p", width: 1920, height: 1080, bitrate: 4000000},
		{name: "portrait_long_edge_is_not_resolution_rank", definition: "720p", width: 720, height: 1280, bitrate: 5000000, wantDynamic: true},
		{name: "real_dimensions_precede_label", definition: "2160p", width: 640, height: 360, bitrate: 10000000, wantDynamic: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			raw := []byte(fmt.Sprintf(`{"code":0,"data":{"video_model":{"video_id":"fixture-video","media_type":"video","video_duration":88.4,"url_expire":1234567890,"video_list":[{"main_url":"","spade_a":"fixture-unused-key"},{"main_url":"https://media.invalid/ordinary.mp4","spade_a":"fixture-ordinary-key","video_meta":{"definition":%q,"vwidth":%d,"vheight":%d,"bitrate":%d}}],"dynamic_video":%s}}}`, tt.definition, tt.width, tt.height, tt.bitrate, fallbackDynamicModel))
			playback, err := ParsePlayback(raw, "7100000000000301")
			if err != nil {
				t.Fatal(err)
			}
			best, err := playback.Select("best")
			if err != nil {
				t.Fatal(err)
			}
			if tt.wantDynamic {
				if len(playback.Streams) != 2 || best.URL != "https://media.invalid/dynamic-1080.mp4" || !best.Dash {
					t.Fatalf("higher dynamic group was not selected: %+v", playback.Streams)
				}
				if !playback.Protected || playback.KeyMaterial != "fixture-dynamic-key" || playback.AudioURL != "https://media.invalid/dynamic-audio.mp4" {
					t.Fatal("dynamic video, audio and protection context were not selected together")
				}
			} else {
				if len(playback.Streams) != 1 || best.URL != "https://media.invalid/ordinary.mp4" || best.Dash {
					t.Fatalf("ordinary group was replaced or mixed: %+v", playback.Streams)
				}
				if !playback.Protected || playback.KeyMaterial != "fixture-ordinary-key" || playback.AudioURL != "" {
					t.Fatal("ordinary group inherited another group's audio or protection context")
				}
			}
			if playback.EpisodeID != "7100000000000301" || playback.VideoID != "fixture-video" || playback.MediaType != "video" || playback.DurationMS != 88400 || playback.DurationSeconds != 88 || playback.ExpiresAt != 1234567890 {
				t.Fatal("group selection lost shared episode metadata")
			}
		})
	}
}

func TestParsePlaybackClearDynamicDoesNotInheritOrdinaryProtection(t *testing.T) {
	dynamic := strings.ReplaceAll(fallbackDynamicModel, `"spade_a":"fixture-dynamic-key",`, "")
	raw := []byte(fmt.Sprintf(`{"code":0,"data":{"video_model":{"video_list":[{"main_url":"https://media.invalid/ordinary-360.mp4","spade_a":"fixture-ordinary-key","video_meta":{"definition":"360p","vwidth":640,"vheight":360,"bitrate":400000}}],"dynamic_video":%s}}}`, dynamic))
	playback, err := ParsePlayback(raw, "7100000000000301")
	if err != nil {
		t.Fatal(err)
	}
	best, err := playback.Select("best")
	if err != nil || len(playback.Streams) != 2 || best.URL != "https://media.invalid/dynamic-1080.mp4" {
		t.Fatalf("dynamic selection = %+v err = %v", best, err)
	}
	if playback.Protected || playback.KeyMaterial != "" || best.Protected || playback.AudioURL != "https://media.invalid/dynamic-audio.mp4" {
		t.Fatal("clear dynamic group inherited ordinary protection or lost its audio")
	}
}

func TestParsePlaybackDynamicWithoutDeclaredAudioRemainsUsable(t *testing.T) {
	raw := []byte(`{"code":0,"data":{"video_model":{"video_list":[{"main_url":"https://media.invalid/ordinary.mp4","video_meta":{"vwidth":640,"vheight":360}}],"dynamic_video":{"dynamic_video_list":[{"main_url":"https://media.invalid/dynamic-video-only.mp4","video_meta":{"vwidth":1920,"vheight":1080}}]}}}}`)
	playback, err := ParsePlayback(raw, "7100000000000301")
	if err != nil {
		t.Fatal(err)
	}
	best, err := playback.Select("best")
	if err != nil || best.URL != "https://media.invalid/dynamic-video-only.mp4" || len(playback.Streams) != 1 || playback.AudioURL != "" {
		t.Fatalf("video-only dynamic selection = %+v err = %v", best, err)
	}
}

func TestParsePlaybackKeepsOrdinaryWhenDynamicGroupIsInvalid(t *testing.T) {
	tests := []struct {
		name    string
		dynamic string
	}{
		{name: "missing_dynamic", dynamic: `null`},
		{name: "invalid_dynamic_type", dynamic: `42`},
		{name: "invalid_dynamic_video_list", dynamic: `{"dynamic_video_list":42}`},
		{name: "invalid_dynamic_video_entry", dynamic: strings.Replace(fallbackDynamicModel, `"dynamic_video_list": [`, `"dynamic_video_list": [42,`, 1)},
		{name: "invalid_dynamic_audio_list", dynamic: strings.Replace(fallbackDynamicModel, `"dynamic_audio_list": [{"main_url":"https://media.invalid/dynamic-audio.mp4"}]`, `"dynamic_audio_list":42`, 1)},
		{name: "invalid_dynamic_audio_entry", dynamic: strings.Replace(fallbackDynamicModel, `{"main_url":"https://media.invalid/dynamic-audio.mp4"}`, `42`, 1)},
		{name: "empty_dynamic_audio_url", dynamic: strings.Replace(fallbackDynamicModel, `https://media.invalid/dynamic-audio.mp4`, `  `, 1)},
		{name: "no_dynamic_video_urls", dynamic: strings.ReplaceAll(strings.ReplaceAll(fallbackDynamicModel, `https://media.invalid/dynamic-720.mp4`, ""), `https://media.invalid/dynamic-1080.mp4`, "")},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			// 错误动态组不影响可用普通流，也不向普通组泄漏音轨或密钥。
			raw := []byte(fmt.Sprintf(`{"code":0,"data":{"video_model":{"video_list":[{"main_url":""},{"main_url":"https://media.invalid/ordinary-360.mp4","video_meta":{"definition":"360p","vwidth":360,"vheight":640,"bitrate":400000}}],"dynamic_video":%s}}}`, tt.dynamic))
			playback, err := ParsePlayback(raw, "7100000000000301")
			if err != nil {
				t.Fatal(err)
			}
			best, err := playback.Select("best")
			if err != nil {
				t.Fatal(err)
			}
			if len(playback.Streams) != 1 || best.URL != "https://media.invalid/ordinary-360.mp4" {
				t.Fatalf("ordinary stream was replaced: %+v", playback.Streams)
			}
			if playback.AudioURL != "" || playback.Protected || playback.KeyMaterial != "" {
				t.Fatal("dynamic context leaked into ordinary playback")
			}
		})
	}
}

func TestParsePlaybackRejectsInvalidDynamicWithoutOrdinary(t *testing.T) {
	for _, dynamic := range []string{
		`42`,
		`{"dynamic_video_list":42}`,
		strings.Replace(fallbackDynamicModel, `{"main_url":"https://media.invalid/dynamic-audio.mp4"}`, `42`, 1),
		strings.Replace(fallbackDynamicModel, `https://media.invalid/dynamic-audio.mp4`, ` `, 1),
	} {
		raw := []byte(fmt.Sprintf(`{"code":0,"data":{"video_model":{"dynamic_video":%s}}}`, dynamic))
		if _, err := ParsePlayback(raw, "7100000000000301"); err == nil {
			t.Fatal("invalid dynamic group was accepted without a usable ordinary group")
		}
	}
}

func TestParsePlaybackFallbackPreservesSingleListModels(t *testing.T) {
	tests := []struct {
		name      string
		fixture   string
		wantAudio string
	}{
		{name: "ordinary_only", fixture: "clear-playback.json"},
		{name: "dynamic_only", fixture: "dynamic-playback.json", wantAudio: "https://media.invalid/movie-audio.mp4"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			playback, err := ParsePlayback(fixture(t, tt.fixture), "7100000000000301")
			if err != nil {
				t.Fatal(err)
			}
			best, err := playback.Select("best")
			if err != nil || len(playback.Streams) != 2 || best.Definition != "1080p" || playback.AudioURL != tt.wantAudio {
				t.Fatalf("single-list playback changed: streams=%d best=%+v audio=%q err=%v", len(playback.Streams), best, playback.AudioURL, err)
			}
		})
	}
}

func TestParsePlaybackFallbackStillRejectsInvalidOrdinaryList(t *testing.T) {
	raw := []byte(fmt.Sprintf(`{"code":0,"data":{"video_model":{"video_list":42,"dynamic_video":%s}}}`, fallbackDynamicModel))
	if _, err := ParsePlayback(raw, "7100000000000301"); err == nil {
		t.Fatal("invalid ordinary list was accepted")
	}
}
