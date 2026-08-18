#!/usr/bin/env bash
# Two persistent llama-server processes. The slots are the point: each keeps its
# KV cache warm between ticks, so only the delta gets prefilled.
#
# Model choice: Gemma 4 26B-A4B rather than Qwen3.6-35B-A3B. Qwen is the stronger
# agentic model, but this harness's structured output is a five-field JSON object
# that either handles. What's actually hard here is the reflect tick — rereading
# weeks of your own writing and deciding whether you still mean it — and Gemma is
# consistently rated better at English prose. It's also ~4GB smaller, which buys
# warm slots. Swap to Qwen if your agent turns out to live on tool-heavy work.
set -euo pipefail

MAIN_MODEL=${MAIN_MODEL:-/var/lib/models/gemma-4-26B-A4B-it-Q4_K_M.gguf}
PULSE_MODEL=${PULSE_MODEL:-/var/lib/models/gemma-4-E4B-it-Q4_K_M.gguf}
THREADS=${THREADS:-$(nproc)}

# Unix sockets, not TCP loopback: llama-server listens on one when --host ends
# in .sock. Matches agent.json's llm.mainSocketPath / llm.pulseSocketPath.
SOCKET_DIR=${SOCKET_DIR:-/run/cozy-harness}
MAIN_SOCKET=${MAIN_SOCKET:-$SOCKET_DIR/llama-main.sock}
PULSE_SOCKET=${PULSE_SOCKET:-$SOCKET_DIR/llama-pulse.sock}
mkdir -p "$SOCKET_DIR"
# llama-server binds rather than unlinking first, so a socket left behind by an
# unclean exit would otherwise fail the next start with "address already in use".
rm -f "$MAIN_SOCKET" "$PULSE_SOCKET"

# Multi-token prediction: ~1.4-2.2x faster generation, no accuracy loss, ~1GB
# extra RAM. Needs MTP GGUFs and a llama.cpp build with MTP merged. Flag names
# have been moving — check `llama-server --help` before turning this on.
MTP_FLAGS=${MTP_FLAGS:-}

# 4 slots x 16K context. q8_0 KV roughly halves cache memory at negligible cost.
llama-server \
  --model "$MAIN_MODEL" \
  --host "$MAIN_SOCKET" \
  --threads "$THREADS" \
  --ctx-size 65536 \
  --parallel 4 \
  --cache-type-k q8_0 --cache-type-v q8_0 \
  --mlock \
  $MTP_FLAGS &

# E4B rather than E2B: the pulse still emits a structured decision, and E4B
# roughly doubles E2B's tool-use score for about 5GB. No MTP — the pulse
# generates ~20 tokens, so drafting buys nothing.
llama-server \
  --model "$PULSE_MODEL" \
  --host "$PULSE_SOCKET" \
  --threads "$THREADS" \
  --ctx-size 8192 \
  --parallel 1 \
  --mlock &

wait
