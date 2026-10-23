# Repository working rules

- Develop directly on `main`. Do not create a branch or pull request unless the user explicitly asks for one.
- The development series started at `0.4.0`; read `Version.props` for the current version. Use a patch increment for small fixes and a minor increment for substantial feature additions.
- Any major-version increment requires the user's explicit confirmation, including `0.x` to `1.x`. Never infer that confirmation from a request to ship or make the project public. Update `version-policy.json` only after that confirmation.
- Every `0.x` GitHub release is a prerelease, including numeric tags without a `-beta` suffix. Do not describe development builds as stable or final releases.
- Pushing a version tag publishes a Release only after CI passes. Do not push a tag or publish a Release unless the user requests it.
- Preserve existing libraries, Preview directories, custom metadata and cached audio during changes and packaging. Explicit user instructions take precedence over these repository defaults.

## YouTrack task tracking

- This repository uses `https://moonflow.youtrack.cloud`, project `VstiSoundPreviewer`, project key `VSP`, for task planning and tracking.
- Use the `managing-youtrack` skill and the existing authenticated YouTrack MCP tools. Reuse the locally configured credentials; never store tokens in this repository.
- Scope issue searches to `project: VSP`. MoonflowTechDemo has separate projects and field definitions; do not reuse its project filters or `Stage` field here.
- Unless the user specifies an issue, select pending issues by highest priority first, breaking ties by ascending numeric issue order (`VSP-1`, `VSP-2`, ...), respecting dependencies.
- After implementation and automated checks, move the issue to `验证中`. Only the user moves it to `Done` after manual acceptance; do not mark implemented issues as completed yourself.
- When asked to work from YouTrack, refresh the relevant issues and read their descriptions, comments, attachments, priorities, due dates and dependencies before implementation. Use the current VSP field schema for updates.
