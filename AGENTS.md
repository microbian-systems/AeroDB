# AeroDB.SurrealDb.EfCore Project Guidelines

## Tech Stack
- **Backend framework**: .net core (.net 10+)
- **Service Layer**: Orleans - main services layer
- **Data persistence**: MartenDB, Postgres
- **ORM**: Entityframework Core (SurrealDB)
- Scalar for API
- **Patterns**:
    - HTMX.NET for server-side interactivity.
- Open Telemetry using serilog and openobserve (serilog sinkg for openobserve)

## Frontend
- Prefer Blazor/Razor over JS libs/frameworks
- If creating Razor/Blazor/Cshtml files - always prefer code behinds instead of inline code
- **Language**: TypeScript first (using `Microsoft.Typescript.MSBuild` for compilation).
- **Strategy**: Using CDN first.
- **CSS Framework**: Tailwind CSS
- **UI Components**: Radzen Blazor
    - HTML/Markdown editor: radzen wysiwyg editor
    - Markdig for markdown rendering
- **JS Libraries**:
    - htmx
    - alpinejs
    - preact

## Coding / Architectural Patterns
- Use GoF patterns and SOLID
    - Make use of Decorator, Visitor, Strategy, Observer and Factories abundantly but only where it makes sense to doo so
- Make use of DDD "lite" 
    - We don't need to go all in but use the basics and foundations to help with clean architecture


## Testing
- **Unit Testing**: TUnit
- **GUI Integration Testing**: Microsoft Playwright
- Use Alba for any asp.net core integration testing
- Use nsubstitute, autofixture and fakeiteasy for mocking (mainly nsubstitute, fakeiteasy when beneficial)
- Use nuget pkg bogus for fake data
- **Assertions**: Use **Shouldly** for assertions. **Do NOT use FluentAssertions**.


## Git Submodules 
./wolverine - the wolverinefx code dir (do not modify)
./marten - the martendb code dir (do not modify)
./surrealdb.net - the surrealdb official client library (do not modify)

## Constraints & Rules
- use socratic method for guidance if something isn't clear
- DO NOT COMMIT Changes to git unless explicitly told to do so
- **DO NOT USE NPM**. All frontend dependencies should align with the CDN usage or libman`Microsoft.Typescript.Build` constraints.
- Testing Constraints
    - Do not use Moq
    - Do not use XUnit, NUnit, MSTest unless explicitly directed to do so
- Project includes a .NET MAUI hybrid web and mobile setup (newly created).
- All APIs should make use of minimal apis over mvc
- Avoid using Guids for primary keys, use Snowflake instead (where possible)
- Do not use newtonsoft.json (use system.text.json)
- all models to be saved to the database should make use of hte IEntity<long> or Entity (which inherits from IEntity<long>)
- FluentValidation is to be usd for validation of all models
- primary keys should be of type long unless explicitly needed otherwise.  The primary key can be generated using Snowflake.NewId()
- always use SOLID principles in the design of the code
- if something is unclear always refer to the ../docs documentation for clarity 
- take the socratic method and ask any architectural code decisions to me
- Avoid using reflection; prefer source generators for code discovery and generation
- for sample images on web pages use: Pexels client (PexelsService) in the aero.services project namespace
- Follow existing project structure and naming conventions.
- Do not introduce unnecessary abstractions.
- Do not change unrelated logic.

## MCP Servers

- **SurrealDB MCP** — linked but requires agent session restart to activate. After reboot, use for SurrealDB schema exploration, SurrealQL syntax validation, and live query testing.
- **mslearn (Microsoft Learn)** — use for .NET / C# / ASP.NET Core / EF Core guidance and code samples. Prefer this over web search for Microsoft technology questions.
- **ctx7 (Context7)** — use for third-party library and framework documentation (SurrealDB, etc.). Resolves official docs and code snippets.

## Important Docs

Agents should read relevant docs before generating code.

- `docs/`
- `docs/efcore-plan.md` — architectural plan for the SurrealDB EF Core provider


## Skills

Use the module creation skill when creating a new Aero CMS module:


