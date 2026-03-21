# Local LLM Setup Guide

VibeModel can connect to a local LLM server running on your machine. This gives you free, private AI chat inside Revit — no API key or internet required.

## Quick Start

1. Start a local LLM server (see options below)
2. In Revit, open VibeModel Chat and click **Settings**
3. Select the **Local LLM** tab
4. Enter the server endpoint (e.g., `http://localhost:8080`)
5. Click **Test Connection** to verify
6. Click **Save**

## Server Options

### llama.cpp (Recommended for power users)

Best performance, most control. Download from: https://github.com/ggerganov/llama.cpp/releases

```bash
# Download a model (e.g., from huggingface.co)
# Start the server with GPU acceleration
./llama-server.exe -m qwen2.5-7b-instruct-q4_k_m.gguf --port 8080 -ngl 99
```

**Endpoint:** `http://localhost:8080`
**Minimum version:** b2500+

### Ollama (Easiest setup)

One-click install, automatic model management. Download from: https://ollama.com

```bash
# Pull a model
ollama pull qwen2.5:7b

# Server starts automatically, or:
ollama serve
```

**Endpoint:** `http://localhost:11434`
**Minimum version:** 0.3+

### LM Studio (GUI-based)

Visual interface for downloading and running models. Download from: https://lmstudio.ai

1. Open LM Studio
2. Download a model from the built-in browser
3. Go to the Server tab and click **Start Server**

**Endpoint:** `http://localhost:1234`
**Minimum version:** 0.3+

## Recommended Models

| Model | Download Size | Tool Use | Quality | Speed |
|-------|:------------:|:--------:|:-------:|:-----:|
| Qwen 2.5 1.5B Instruct | ~1 GB | No | Basic chat | Fast |
| Qwen 2.5 7B Instruct | ~4 GB | Yes | Good | Medium |
| Mistral 7B Instruct | ~4 GB | Yes | Good | Medium |
| Qwen 2.5 14B Instruct | ~8 GB | Yes | Great | Slower |

**For Revit commands** (creating walls, modifying elements, etc.), you need a model that supports function/tool calling — typically 7B parameters or larger. Smaller models work fine for general chat and Q&A.

## Tool Use (Revit Commands)

By default, the local LLM backend runs in **chat-only mode**. To enable Revit commands:

1. Use a model that supports function calling (7B+ recommended)
2. In Settings > Local LLM, check **Enable tool use (Revit commands)**
3. Save and try a command like "List all walls"

Tool use quality depends heavily on the model. Claude and larger models (14B+) will handle complex multi-step operations better than 7B models.

## Timeout Configuration

Local models can be much slower than cloud APIs, especially on CPU or with limited VRAM. If responses are timing out:

- Adjust the **Response timeout** slider in Settings (default: 120s, max: 300s)
- CPU-only inference on 7B+ models may need 180-300s
- GPU inference with sufficient VRAM typically responds in 10-30s

## Hardware Requirements

Running Revit and a local LLM simultaneously requires decent hardware:

| Model Size | Min VRAM | Min RAM (CPU only) |
|:----------:|:--------:|:------------------:|
| 1.5B (Q4) | 2 GB | 4 GB |
| 7B (Q4) | 6 GB | 8 GB |
| 14B (Q4) | 10 GB | 16 GB |

**Tip:** If your GPU VRAM is limited, use a smaller quantization (Q4_K_M) or run a smaller model. Revit itself uses significant RAM, so leave headroom.

## Troubleshooting

**"Could not reach server"**
- Verify the server is running and the port is correct
- Check that no firewall is blocking localhost connections
- Try opening the endpoint in a browser (e.g., `http://localhost:8080/health`)

**Slow responses**
- Increase the timeout slider in Settings
- Enable GPU offloading (`-ngl 99` in llama.cpp)
- Use a smaller model or more aggressive quantization

**Tool calls not working**
- Verify tool use is enabled in Settings
- Try a model known to support function calling (Qwen 2.5 7B+)
- Check VibeModel logs at `%LOCALAPPDATA%\VibeModel\logs\` for errors
