# Repository working rules

- Develop directly on `main`. Do not create a branch or pull request unless the user explicitly asks for one.
- The current development version starts at `0.4.0`. Use a patch increment for small fixes and a minor increment for substantial feature additions.
- Any major-version increment requires the user's explicit confirmation, including `0.x` to `1.x`. Never infer that confirmation from a request to ship or make the project public. Update `version-policy.json` only after that confirmation.
- Every `0.x` GitHub release is a prerelease, including numeric tags without a `-beta` suffix. Do not describe development builds as stable or final releases.
- Pushing a version tag publishes a Release only after CI passes. Do not push a tag or publish a Release unless the user requests it.
- Preserve existing libraries, Preview directories, custom metadata and cached audio during changes and packaging. Explicit user instructions take precedence over these repository defaults.
