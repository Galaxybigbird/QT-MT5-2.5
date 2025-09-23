package main

import (
	"bufio"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"regexp"
	"sort"
	"strconv"
	"strings"
)

type logLine struct {
	Message string `json:"message"`
	Level   string `json:"level"`
	Source  string `json:"source"`
	Comp    string `json:"component"`
}

var (
	markersOfInterest = []string{
		"TRAILING_ORDER_ACK_METRICS",
		"UNIFIED_TRAILING_METRICS",
		"TRAILING_ORDER",
		"TRAILING_MOD_COALESCE",
		"UNIFIED_STOP_CALC",
		"TRAILING_UPDATE_START",
		"TRAILING_ORDER_ACTIVE",
		"TRAILING_TTL_RESEND",
		"CRITICAL_ERROR",
		"ERROR",
	}
)

// regexes to extract fields from message payloads
var (
	ackLatencyRe      = regexp.MustCompile(`ack_latency_ms=([0-9]+)`)      // integer ms
	lastSubmitUtcRe   = regexp.MustCompile(`last_submit_utc=([^\s]+)`)     // ISO string
	appliedGapTicksRe = regexp.MustCompile(`applied_gap_ticks=([-0-9.]+)`) // float
	baseIDRe          = regexp.MustCompile(`^(\w+):\s+([^\s]+)`)           // "MARKER: BASE_ID ..."
)

func main() {
	file := flag.String("file", "", "Path to JSONL log file (unified-*.jsonl)")
	flag.Parse()
	if *file == "" {
		fmt.Println("usage: analyze -file <path-to-jsonl>")
		os.Exit(2)
	}
	f, err := os.Open(*file)
	if err != nil {
		fmt.Fprintf(os.Stderr, "failed to open file: %v\n", err)
		os.Exit(1)
	}
	defer f.Close()

	counts := map[string]int{}
	ackLatencies := []int{}
	ackZero := 0
	ackNonZero := 0
	ackMissingLastSubmit := 0
	gapSamples := 0
	gapSum := 0.0
	gapMax := -1e9
	gapMin := 1e9
	perBaseAck := map[string]int{}

	scanner := bufio.NewScanner(f)
	scanner.Buffer(make([]byte, 0, 1024*1024), 1024*1024) // allow long lines
	lineNum := 0
	for scanner.Scan() {
		lineNum++
		b := scanner.Bytes()
		// fast path: only consider lines containing \"message\"
		if !strings.Contains(string(b), "\"message\"") {
			continue
		}
		var ll logLine
		if err := json.Unmarshal(b, &ll); err != nil {
			continue
		}
		msg := ll.Message
		if msg == "" {
			continue
		}
		colon := strings.Index(msg, ":")
		if colon <= 0 {
			continue
		}
		marker := strings.TrimSpace(msg[:colon])
		counts[marker]++

		// BaseID extraction (best-effort)
		base := ""
		if m := baseIDRe.FindStringSubmatch(msg); len(m) == 3 {
			base = m[2]
		}

		// Ack metrics parsing
		if marker == "TRAILING_ORDER_ACK_METRICS" {
			if m := ackLatencyRe.FindStringSubmatch(msg); len(m) == 2 {
				if v, err := strconv.Atoi(m[1]); err == nil {
					ackLatencies = append(ackLatencies, v)
					if v == 0 {
						ackZero++
					} else {
						ackNonZero++
					}
				}
			}
			if m := lastSubmitUtcRe.FindStringSubmatch(msg); len(m) == 2 {
				if strings.HasPrefix(m[1], "0001-") {
					ackMissingLastSubmit++
				}
			}
			if base != "" {
				perBaseAck[base]++
			}
		}

		// Trailing metrics parsing
		if marker == "UNIFIED_TRAILING_METRICS" {
			if m := appliedGapTicksRe.FindStringSubmatch(msg); len(m) == 2 {
				if v, err := strconv.ParseFloat(m[1], 64); err == nil {
					gapSum += v
					gapSamples++
					if v > gapMax {
						gapMax = v
					}
					if v < gapMin {
						gapMin = v
					}
				}
			}
		}
	}
	if err := scanner.Err(); err != nil {
		fmt.Fprintf(os.Stderr, "scan error: %v\n", err)
	}

	// Print summary
	fmt.Println("-- Marker counts --")
	// Ensure consistent order: first known markers, then any others
	printed := map[string]bool{}
	for _, k := range markersOfInterest {
		if counts[k] > 0 {
			fmt.Printf("%s=%d\n", k, counts[k])
			printed[k] = true
		}
	}
	// Print any extra markers encountered
	extras := make([]string, 0)
	for k := range counts {
		if !printed[k] {
			extras = append(extras, k)
		}
	}
	sort.Strings(extras)
	for _, k := range extras {
		fmt.Printf("%s=%d\n", k, counts[k])
	}

	fmt.Println("\n-- ACK metrics --")
	fmt.Printf("ack_total=%d ack_non_zero=%d ack_zero=%d missing_last_submit_utc=%d\n", len(ackLatencies), ackNonZero, ackZero, ackMissingLastSubmit)
	if len(ackLatencies) > 0 {
		sort.Ints(ackLatencies)
		sum := 0
		for _, v := range ackLatencies {
			sum += v
		}
		avg := float64(sum) / float64(len(ackLatencies))
		p50 := ackLatencies[len(ackLatencies)/2]
		p90 := ackLatencies[int(float64(len(ackLatencies))*0.9)]
		p99 := ackLatencies[int(float64(len(ackLatencies))*0.99)]
		fmt.Printf("ack_ms_avg=%.1f p50=%d p90=%d p99=%d\n", avg, p50, p90, p99)
	}

	fmt.Println("\n-- Applied gap ticks (UNIFIED_TRAILING_METRICS) --")
	if gapSamples > 0 {
		avgGap := gapSum / float64(gapSamples)
		fmt.Printf("samples=%d avg=%.2f min=%.2f max=%.2f\n", gapSamples, avgGap, gapMin, gapMax)
	} else {
		fmt.Println("no samples")
	}

	if len(perBaseAck) > 0 {
		fmt.Println("\n-- ACKs per BaseID (top 10) --")
		type kv struct {
			k string
			v int
		}
		arr := make([]kv, 0, len(perBaseAck))
		for k, v := range perBaseAck {
			arr = append(arr, kv{k, v})
		}
		sort.Slice(arr, func(i, j int) bool { return arr[i].v > arr[j].v })
		for i := 0; i < len(arr) && i < 10; i++ {
			fmt.Printf("%s=%d\n", arr[i].k, arr[i].v)
		}
	}
}
