## Summary

Describe the user-visible change and why it is needed.

## Compatibility and safety

- [ ] Source media and source subtitles remain unchanged.
- [ ] Jellyfin ABI, platform, model, permission, and subtitle-format impacts are documented.
- [ ] No real server names, paths, addresses, media data, credentials, or logs are included.
- [ ] Third-party license and attribution requirements remain satisfied.

## Validation

- [ ] `dotnet format Jellyfin.Plugin.SubtitleAligner.slnx --verify-no-changes`
- [ ] `dotnet build Jellyfin.Plugin.SubtitleAligner.slnx --configuration Release --no-restore`
- [ ] Dashboard JavaScript syntax checks pass.
- [ ] Documentation and changelog are updated when behavior changes.
