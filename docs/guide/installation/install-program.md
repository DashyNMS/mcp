# Run it without Docker

From 2.0 there's one program for each kind of computer, with nothing else to install.

1. Open the [releases page](https://github.com/DashyNMS/mcp/releases). Until 2.0 is released, choose a nightly build.
2. Download the archive for your computer, and `SHA256SUMS`:

   | Computer | Archive |
   | --- | --- |
   | Windows | `dashynms-mcp-<version>-win-x64.zip` or `-win-arm64.zip` |
   | Mac, Apple silicon | `dashynms-mcp-<version>-osx-arm64.tar.gz` |
   | Mac, Intel | `dashynms-mcp-<version>-osx-x64.tar.gz` |
   | Linux | `dashynms-mcp-<version>-linux-x64.tar.gz` or `-linux-arm64.tar.gz` |

3. Check the download, from the folder it's in:

   ```bash
   sha256sum --check --ignore-missing SHA256SUMS
   ```

   On a Mac, use `shasum -a 256 --check --ignore-missing SHA256SUMS`. On Windows, run `Get-FileHash` on the archive in PowerShell and compare it with the line in `SHA256SUMS`.

4. Unpack it anywhere and note its full path.
5. Continue with [Connect your assistant](../how-to/connect-a-client.md).

The Mac programs are signed and notarised. The Windows ones aren't signed yet, so Windows may ask before running one the first time. On Alpine Linux, use [Docker](install-docker.md).

To update, replace the program with a newer one and restart your assistant.
