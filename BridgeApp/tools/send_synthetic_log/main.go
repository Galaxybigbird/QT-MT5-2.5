package main

import (
	"context"
	"flag"
	"fmt"
	"time"

	trading "BridgeApp/internal/grpc/proto"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
)

func main() {
	addr := flag.String("addr", "127.0.0.1:50051", "gRPC server address")
	msg := flag.String("msg", "split-check base_id=TEST_A and then base_id=TEST_B", "log message")
	src := flag.String("src", "synthetic_tester", "log source")
	lvl := flag.String("lvl", "INFO", "log level")
	flag.Parse()

	conn, err := grpc.Dial(*addr, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		panic(err)
	}
	defer conn.Close()

	client := trading.NewLoggingServiceClient(conn)
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()

	evt := &trading.LogEvent{
		TimestampNs: time.Now().UnixNano(),
		Source:      *src,
		Level:       *lvl,
		Component:   "split_test",
		Message:     *msg,
	}
	ack, err := client.Log(ctx, evt)
	if err != nil {
		panic(err)
	}
	fmt.Printf("sent synthetic event; accepted=%d dropped=%d\n", ack.GetAccepted(), ack.GetDropped())
}
