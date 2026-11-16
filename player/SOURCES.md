# Audio engine

The Windows interface is native C# / Windows Forms. The audio worker uses the standard Media Source Extensions and Encrypted Media Extensions APIs with the unmodified Widevine CDM installed by Electron for Content Security.

- [Electron for Content Security](https://github.com/castlabs/electron-releases), version 45.2.1+wvcus, Windows x64. Original runtime licences remain in `runtime/ECS` in portable builds.
- [Castlabs EVS](https://github.com/castlabs/electron-releases/wiki/EVS): production VMP signing is performed by the build author. End users do not need an EVS account. Signing credentials are not distributed.
- [Stopify](https://github.com/Anattana-Labs/Stopify): reference for the web player's Dealer, device registration, manifests, state reporting and persisted catalogue query identifiers. No embedded account tokens or DRM keys from that repository are used.
- [Media Source Extensions](https://developer.mozilla.org/en-US/docs/Web/API/MediaSource), [Encrypted Media Extensions](https://developer.mozilla.org/en-US/docs/Web/API/Encrypted_Media_Extensions_API).
- [Spotify's official web player](https://open.spotify.com/): current persisted catalogue operation identifiers and mutation variable names were checked against its publicly served web-player JavaScript on 2026-11-16. These are public operation identifiers, not account credentials.
- [Discord RPC](https://discord.com/developers/docs/topics/rpc): local named-pipe Rich Presence. The Application ID in config.json is public; no Discord user token or bot token is used.

Only the signed runtime is distributed. Widevine installs and updates through its normal component updater in the user's application data. The application uses the user's own Spotify session and Spotify's media and licence servers; DRM keys stay in the CDM. The implementation does not filter ads or substitute another music source.

For a source build, use prepare-player.ps1 to download the official ECS release and verify the pinned SHA-256. The build author then applies a production VMP signature using Castlabs EVS as documented above. The portable package includes the already signed runtime and its original licences. Do not change the runtime executable after signing. A Windows Authenticode publisher certificate is a separate future task.

The desktop icon is branding/yspotify.ico. tools/build-icon.ps1 resizes and encodes branding/yspotify-avatar-v1.png into the Windows ICO. The header and icon use the same Utsuho motif.
