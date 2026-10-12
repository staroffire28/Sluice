# Contributing to Sluice

Thanks for your interest! Bug reports, ideas and pull requests are welcome. Please follow our [Code of Conduct](CODE_OF_CONDUCT.md).

## Getting started

Requirements: .NET 10 SDK.

```bash
git clone https://github.com/staroffire28/Sluice.git
cd Sluice
dotnet build Sluice.Portal.sln -c Release
```

See the [user guide](docs/guia-usuario/README.md) for how to run the portal.

## Pull requests

- Open an issue first for large changes so we can agree on the approach.
- Keep each PR focused on one change.
- Make sure `dotnet build` succeeds before pushing.
- Never commit secrets: `.env` and `appsettings.Development.json` are ignored on purpose.

## License

By contributing you agree that your contributions are licensed under the
[Apache License 2.0](LICENSE).

## Support the project

If Sluice is useful to you, you can support its development on
[Ko-fi](https://ko-fi.com/staroffire28).
