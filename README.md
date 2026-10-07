[![SWUbanner](https://raw.githubusercontent.com/vshymanskyy/StandWithUkraine/main/banner2-direct.svg)](https://github.com/vshymanskyy/StandWithUkraine/blob/main/docs/README.md)

# SourceAFIS for .NET

[![Build status](https://github.com/RogerioKoglin/sourceafis-net/actions/workflows/build.yml/badge.svg)](https://github.com/RogerioKoglin/sourceafis-net/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/v/RogerioKoglin.SourceAFIS)](https://www.nuget.org/packages/RogerioKoglin.SourceAFIS/)

SourceAFIS for .NET is a pure C# port of [SourceAFIS](https://sourceafis.machinezoo.com/),
an algorithm for recognition of human fingerprints.
It can compare two fingerprints 1:1 or search a large database 1:N for matching fingerprint.
It takes fingerprint images on input and produces similarity score on output.
Similarity score is then compared to customizable match threshold.

More on [homepage](https://sourceafis.machinezoo.com/net).

## Status

This is Rogerio Koglin's development fork of the stable SourceAFIS for .NET project.
The public package produced by this fork is named `RogerioKoglin.SourceAFIS`, while
the assembly and namespaces remain `SourceAFIS` for source compatibility.

## Getting started

See [homepage](https://sourceafis.machinezoo.com/net).

## Documentation

* [SourceAFIS for .NET](https://sourceafis.machinezoo.com/net)
* [XML doc comments](https://github.com/RogerioKoglin/sourceafis-net/tree/master/SourceAFIS)
* [SourceAFIS overview](https://sourceafis.machinezoo.com/)
* [Algorithm](https://sourceafis.machinezoo.com/algorithm)

## Packages

Pushes to `master` are built, tested, and packed as uniquely versioned CI artifacts
by GitHub Actions. Stable packages are published to NuGet.org when a SemVer tag is
pushed, for example:

```bash
git tag v3.14.0-rk.1
git push origin v3.14.0-rk.1
```

The `release` workflow requires a repository secret named `NUGET_TOKEN` containing
a NuGet.org API key authorized to publish `RogerioKoglin.SourceAFIS`.

## Feedback

Bug reports and pull requests are welcome. See [CONTRIBUTING.md](https://github.com/RogerioKoglin/sourceafis-net/blob/master/CONTRIBUTING.md).

## License

Distributed under [Apache License 2.0](https://github.com/RogerioKoglin/sourceafis-net/blob/master/LICENSE).
