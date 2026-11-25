# Run it with Docker

The container image runs on Intel, AMD and ARM computers. It's about 45 MB to download.

```bash
docker pull ghcr.io/dashynms/mcp:latest
```

Your assistant starts the container for you, so there's nothing to leave running. Continue with [Connect your assistant](../how-to/connect-a-client.md). To run one copy for several people, see [Share one server over HTTP](../how-to/share-over-http.md).

## Choose a version

| Tag | What it is |
| --- | --- |
| `latest` | The newest release |
| `1.1`, `1.1.0` | A release line, or one exact release |
| `nightly` | Last night's build, with newer features |
| `edge` | A build of every change; it may not work |

2.0 hasn't been released yet, so `latest` is still 1.1. Use `nightly` for anything this guide describes that 1.1 doesn't have.

## Update

Docker keeps using the copy it has. Pull again, then restart your assistant:

```bash
docker pull ghcr.io/dashynms/mcp:latest
```
