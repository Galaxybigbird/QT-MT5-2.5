package main

import "testing"

func TestGetElasticProfitLevel_MapAliases(t *testing.T) {
	cases := []map[string]interface{}{
		{"ProfitLevel": 2.0},
		{"profit_level": 3.0},
		{"profitLevel": 4.0},
		{"ElasticProfitLevel": 5.0},
		{"elastic_profit_level": 6.0},
		{"elasticProfitLevel": 7.0},
		{"level": 8.0},
		{"profit_level": "9"},
	}
	expected := []int{2, 3, 4, 5, 6, 7, 8, 9}
	for i, m := range cases {
		got := getElasticProfitLevel(m)
		if got != expected[i] {
			t.Fatalf("case %d: expected %d, got %d for map %v", i, expected[i], got, m)
		}
	}
}

func TestGetElasticCurrentProfit_MapAliases(t *testing.T) {
	cases := []map[string]interface{}{
		{"CurrentProfit": 12.34},
		{"current_profit": 23.45},
		{"currentProfit": 34.56},
		{"ElasticCurrentProfit": 45.67},
		{"elastic_current_profit": 56.78},
		{"elasticCurrentProfit": 67.89},
		{"profit": 78.9},
		{"current_profit": "89.01"},
	}
	expected := []float64{12.34, 23.45, 34.56, 45.67, 56.78, 67.89, 78.9, 89.01}
	for i, m := range cases {
		got := getElasticCurrentProfit(m)
		if (got-expected[i]) > 1e-9 || (expected[i]-got) > 1e-9 {
			t.Fatalf("case %d: expected %.4f, got %.4f for map %v", i, expected[i], got, m)
		}
	}
}

// Struct types mimicking internal/grpc InternalElasticHedgeUpdate shape variants
type elasticStructA struct {
	ProfitLevel   int32
	CurrentProfit float64
}
type elasticStructB struct {
	ElasticProfitLevel   int32
	ElasticCurrentProfit float64
}

func TestGetElastic_FromStructs(t *testing.T) {
	a := elasticStructA{ProfitLevel: 11, CurrentProfit: 111.1}
	if lv := getElasticProfitLevel(a); lv != 11 {
		t.Fatalf("expected 11, got %d", lv)
	}
	if cp := getElasticCurrentProfit(a); (cp - 111.1) > 1e-9 {
		t.Fatalf("expected 111.1, got %.4f", cp)
	}

	b := elasticStructB{ElasticProfitLevel: 12, ElasticCurrentProfit: 222.2}
	if lv := getElasticProfitLevel(b); lv != 12 {
		t.Fatalf("expected 12, got %d", lv)
	}
	if cp := getElasticCurrentProfit(b); (cp - 222.2) > 1e-9 {
		t.Fatalf("expected 222.2, got %.4f", cp)
	}
}
