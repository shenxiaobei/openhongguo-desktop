package sdk

import (
	"context"
	"fmt"

	"github.com/beilunyang/openhongguo/sdk/model"
)

// SeriesSeasons 只读取服务端明确关联的系列剧列表，不按相似标题搜索补全。
// 没有系列剧行时返回空列表；账号、验证、限流、网络等错误原样返回。
func (c *Client) SeriesSeasons(ctx context.Context, seriesID string) ([]model.Season, error) {
	return withFreshDevice(ctx, c, false, func(ctx context.Context) ([]model.Season, error) {
		if !validID(seriesID) {
			return nil, fmt.Errorf("invalid series ID")
		}
		return c.seriesSeasons(ctx, seriesID)
	})
}
