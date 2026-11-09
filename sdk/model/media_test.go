package model

import "testing"

func TestSelectOrdersByExplicitHeightThenBitrate(t *testing.T) {
	p := &Playback{Streams: []Stream{
		{Definition: "720p", Height: 720, Bitrate: 900, URL: "https://media.invalid/720-low.mp4"},
		{Definition: "720p", Height: 720, Bitrate: 1800, URL: "https://media.invalid/720-high.mp4"},
		{Definition: "1080p", Height: 1080, Bitrate: 3000, URL: "https://media.invalid/1080.mp4"},
		{Definition: "360p", Height: 360, URL: " "},
	}}
	best, err := p.Select("best")
	if err != nil || best.URL != "https://media.invalid/1080.mp4" {
		t.Fatalf("best = %+v err = %v", best, err)
	}
	worst, err := p.Select("worst")
	if err != nil || worst.URL != "https://media.invalid/720-low.mp4" {
		t.Fatalf("worst = %+v err = %v", worst, err)
	}
	// An empty URL is never selected, so the 360p row is skipped.
	if _, err := p.Select("360p"); err == nil {
		t.Fatal("stream without a URL was selected")
	}
	if _, err := p.Select("1080p"); err != nil {
		t.Fatalf("exact definition err = %v", err)
	}
}

func TestSelectReportsAvailableDefinitions(t *testing.T) {
	p := &Playback{Streams: []Stream{{Definition: "540p", URL: "https://media.invalid/a.mp4"}, {Quality: "normal", URL: "https://media.invalid/b.mp4"}}}
	_, err := p.Select("2160p")
	if err == nil {
		t.Fatal("missing quality was accepted")
	}
	if got := err.Error(); got != `quality "2160p" is not offered (available: normal 540p)` {
		t.Fatalf("err = %s", got)
	}
}

func TestSelectSpecificQualityUsesHighestBitrate(t *testing.T) {
	p := &Playback{Streams: []Stream{
		{Definition: "720p", Quality: "normal", Codec: "h264", Width: 1280, Height: 720, Bitrate: 900, URL: "https://media.invalid/720-low.mp4"},
		{Definition: "720p", Quality: "normal", Codec: "h264", Width: 1280, Height: 720, Bitrate: 1800, URL: "https://media.invalid/720-high.mp4"},
		{Definition: "720p", Quality: "normal", Codec: "h264", Width: 1280, Height: 720, Bitrate: 9000, URL: " "},
	}}
	for _, quality := range []string{"720p", "normal", "h264", " 720P ", " NORMAL ", " H264 "} {
		t.Run(quality, func(t *testing.T) {
			stream, err := p.Select(quality)
			if err != nil || stream.Bitrate != 1800 || stream.URL != "https://media.invalid/720-high.mp4" {
				t.Fatalf("同档位未选择最高码率：quality=%q bitrate=%d err=%v", quality, stream.Bitrate, err)
			}
		})
	}
	stream, err := p.Select("worst")
	if err != nil || stream.Bitrate != 900 {
		t.Fatalf("流畅优先选择被改变：bitrate=%d err=%v", stream.Bitrate, err)
	}
}

func TestSelectComparesPortraitAndLandscapeByShortEdge(t *testing.T) {
	p := &Playback{Streams: []Stream{
		{Definition: "540p", Width: 540, Height: 960, Bitrate: 3000, URL: "https://media.invalid/portrait-540.mp4"},
		{Definition: "720p", Width: 1280, Height: 720, Bitrate: 2000, URL: "https://media.invalid/landscape-720.mp4"},
	}}
	best, err := p.Select("best")
	if err != nil || best.Definition != "720p" {
		t.Fatalf("最高分辨率按竖向高度误排：definition=%q err=%v", best.Definition, err)
	}
	worst, err := p.Select("worst")
	if err != nil || worst.Definition != "540p" {
		t.Fatalf("最低分辨率按竖向高度误排：definition=%q err=%v", worst.Definition, err)
	}
}
