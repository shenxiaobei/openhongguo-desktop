package model

// DiscussionItem 是平台实际返回的一条评论或弹幕；缺少的互动计数保持 nil。
type DiscussionItem struct {
	ID        string `json:"id"`
	Author    string `json:"author,omitempty"`
	Text      string `json:"text"`
	AvatarURL string `json:"avatar_url,omitempty"`
	LikeCount *int64 `json:"like_count"`
	// CreatedAt 使用 Unix 秒，0 表示来源未提供有效时间。
	CreatedAt int64 `json:"created_at,omitempty"`
	// PositionMS 仅用于有真实时间轴的弹幕；普通评论保持 nil。
	PositionMS *int64 `json:"position_ms,omitempty"`
	Color      string `json:"color,omitempty"`
}

// DiscussionPage 描述本次真正获取的内容，Total 与 Items 的条数可以不同。
type DiscussionPage struct {
	Items   []DiscussionItem `json:"items"`
	Total   *int64           `json:"total"`
	Cursor  string           `json:"cursor,omitempty"`
	HasMore bool             `json:"has_more"`
	// SourceLimited 表示来源只开放部分预览，不应显示“全部已加载”。
	SourceLimited bool   `json:"source_limited"`
	Source        string `json:"source"`
	Note          string `json:"note,omitempty"`
}
