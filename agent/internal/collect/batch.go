// Package collect собирает метрики и инвентарь хоста и контейнеров.
package collect

import (
	"maps"
	"time"

	"google.golang.org/protobuf/types/known/timestamppb"

	agentv1 "icc/agent/internal/pb/icc/agent/v1"
)

type batch struct {
	ts      *timestamppb.Timestamp
	common  map[string]string
	samples []*agentv1.MetricSample
}

func newBatch(now time.Time, common map[string]string) *batch {
	return &batch{ts: timestamppb.New(now), common: common}
}

func (b *batch) add(name string, value float64, labels map[string]string) {
	all := make(map[string]string, len(b.common)+len(labels))
	maps.Copy(all, b.common)
	maps.Copy(all, labels)
	b.samples = append(b.samples, &agentv1.MetricSample{Name: name, Labels: all, Value: value, Timestamp: b.ts})
}
