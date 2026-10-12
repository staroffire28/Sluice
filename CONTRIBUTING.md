# Contributing to Sluice

Thanks for your interest! Bug reports, ideas and pull requests are welcome.

## Getting started

Requirements: .NET 8 SDK and Docker (SQL Server runs in a container for tests).

```bash
git clone https://github.com/staroffire28/Sluice.git
cd Sluice
dotnet test Sluice.Portal.sln -c Release
```

See the [user guide](docs/guia-usuario/README.md) for how to run the portal.

## Pull requests

- Open an issue first for large changes so we can agree on the approach.
- Keep each PR focused on one change, and add or update tests.
- Make sure `dotnet test` passes before pushing.
- Never commit secrets: `.env` and `appsettings.Development.json` are ignored on purpose.

## License

By contributing you agree that your contributions are licensed under the
[Apache License 2.0](LICENSE).

## Support the project

If Sluice is useful to you, you can support its development on
[Ko-fi](https://ko-fi.com/staroffire28).
