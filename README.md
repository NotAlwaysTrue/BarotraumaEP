# Barotrauma

Copyright © FakeFish Ltd 2017-2026

Before downloading the source code, please read the [EULA](EULA.txt).

If you have a question or an issue to report, please check our [Contribution Guideline](https://github.com/Regalis11/Barotrauma/blob/master/CONTRIBUTING.md).

If you're interested in working on the code, either to develop mods or to contribute something to the repository, you will also find instructions in the [Contribution Guideline](https://github.com/Regalis11/Barotrauma/blob/master/CONTRIBUTING.md).

## EP thread placement

The client and dedicated server accept these launch options independently.

| Option | Effect |
| --- | --- |
| `-corecount 2` | Select the first two available physical cores. |
| `-forceusecore 0,2,4,6` | Select these four physical cores on Windows or Linux. Takes precedence over `-corecount`. |

Core IDs are zero-based. Windows orders physical cores by processor group and core index. Linux orders online physical cores by their first logical CPU. EP retains the IDs when filtering for process affinity, so a restricted process can have gaps in its available IDs. SMT siblings belong to the same physical core.

In automatic mode on a hybrid CPU, EP assigns compute workers to performance cores and dedicated threads to efficiency cores. On homogeneous CPUs, and whenever either option is supplied, each selected core serves one compute worker or up to two dedicated threads. EP keeps at least one compute worker available and shares cores if dedicated threads exceed capacity. Dedicated threads release their allocation when they exit. The main thread has no reserved core, and ordinary .NET background tasks keep their normal scheduler.

On macOS, the OS chooses the actual cores. `-corecount` sets the physical-core budget. With `-forceusecore`, EP prints a warning and continues using the number of IDs as its budget, capped at the available physical-core count.

## Links:

**Official Website:** www.barotraumagame.com

**Steam Forums:** https://steamcommunity.com/app/602960/discussions/

**Discord:** https://discordapp.com/invite/undertow

**Wiki:** https://barotraumagame.com/wiki/Main_Page

## Prerequisities:
### Windows
- [Visual Studio](https://www.visualstudio.com/vs/community/) with C# 10 support (VS 2022 or later recommended)
### Linux
- [.NET 8 SDK](https://docs.microsoft.com/en-us/dotnet/core/install/linux)
### macOS
- [Visual Studio 2022 for Mac](https://visualstudio.microsoft.com/vs/mac/)
