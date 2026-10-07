# Third-party notices

## Functional baseline (concepts only — no source code copied)

**mcp-adr-analysis-server** — https://github.com/tosin2013/mcp-adr-analysis-server
Licensed under the MIT License, Copyright (c) 2025 MCP ADR Analysis Server.

This project used the server's documented tool semantics (ADR discovery, technology detection, drift validation, content
masking, keyword-scored memory, confidence scoring) as a functional baseline and re-implemented them in C# for Azure
Functions. No source code was copied. Should any code be ported verbatim in the future, the MIT permission notice below
must be retained in the affected files.

```
MIT License

Copyright (c) 2025 MCP ADR Analysis Server

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## NuGet dependencies (consumed as packages)

| Package | License |
|---|---|
| Microsoft.Azure.Functions.Worker.*, Microsoft.Azure.Functions.Worker.Extensions.Mcp | MIT (Microsoft) |
| Azure.Identity, Azure.Storage.Blobs, Azure.Storage.Queues, Microsoft.Azure.Cosmos | MIT (Microsoft) |
| Microsoft.Extensions.* | MIT (.NET Foundation) |
| Microsoft.ApplicationInsights.WorkerService | MIT (Microsoft) |
| Newtonsoft.Json 13.0.4 (required by the Cosmos SDK build check; not used for serialization) | MIT (James Newton-King) |
| xunit, xunit.runner.visualstudio | Apache-2.0 |
| NSubstitute | BSD-3-Clause |
| coverlet.collector | MIT |

## Reference material (no code copied)

- Azure-Samples/remote-mcp-functions-dotnet — MIT (Microsoft).
- Microsoft Learn documentation — CC BY 4.0 (text), MIT (code samples).
