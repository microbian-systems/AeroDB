# Aero.Cms Project Guidelines

## Tech Stack
- **Backend framework**: asp.net core (.net 10)
- **Service Layer**: Orleans - main services layer
- **Data persistence**: MartenDB, Postgres
- **Messaging/Workflow**: Wolverine
    - Wolverine FX for event sourcing
- TickerQ for background jobs
- **ORM**: Entityframework Core (npgsql)
- Scalar for API
- **Patterns**:
    - Heavy use of the repository pattern (`IGenericMartenRepository<T>` and `GenericMartenRepository<T>`) between MartenDB and database.
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
- Entity : IEntity<long>; for database entities
    - IEntity<long> { long Id { get; set; } }
    - using Snowflake to assign IDs (Snowflake.NewId())
    - This includes MartenDB entities and EF Core entities and aspnet Identity
- Use the **Railway Oriented Programming** patterns:
    - `Result<T>`
    - `Option<T>`
    - `Bind<T>`
    - `Map<T>`
- Make use of DDD "lite" 
    - We don't need to go all in but use the basics and foundations to help with clean architecture


## Testing
- **Unit Testing**: TUnit
- **GUI Integration Testing**: Microsoft Playwright
- **Integration Testing Resource**: Investigate using [mysticmind-postgresembed](https://github.com/mysticmind/mysticmind-postgresembed) for embedded Postgres in tests.
- Use Alba for any asp.net core integration testing
- Use nsubstitute, autofixture and fakeiteasy for mocking (mainly nsubstitute, fakeiteasy when beneficial)
- Use TUnit for unit testing ()
- Use nuget pkg bogus for fake data
- use embedded postgres for testing: 
    - https://github.com/mysticmind/mysticmind-postgresembed 


## Git Submodules 
- the ./Aero dir is a git submodule and requires its own Directory.Build.props and Directory.Packages.props


## Constraints & Rules
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
- FluentValidatino is to be usd for validation of all models
- primary keys should be of type long unless explicitly needed otherwise.  The primary key can be generated using Snowflake.NewId()
- always use SOLID principles in the design of the code
- Use Railway Oriented Programming for all code that handles business logic and data access (Aero.Core has the Result<T> and Option<T> types along with Bind<T> and Map<T>)
- if something is unclear always refer to the ../docs documentation for clarity 
- take the socratic method and ask any architectural code decisions to me
- Avoid using reflection; prefer source generators for code discovery and generation
- for sample images on web pages use: Pexels client (PexelsService) in the aero.services project namespace

### Module Development Rules
- Do not use reflection-based module discovery.
- Use source generators for module discovery.
- Follow existing project structure and naming conventions.
- Keep module-specific logic inside `Aero.Cms.Modules.[FeatureName]`.
- Do not introduce unnecessary abstractions.
- Do not change unrelated logic.
- Prefer MartenDB for persistence using `GenericMartenRepository` from `Aero/src/Aero.Marten`. Use EF Core (via `GenericEntityFrameworkRepository` from `Aero/src/Aero.EfCore`) only when the domain requires relational access or Identity.

## Important Docs

Agents should read relevant docs before generating code.

- `docs/`
- `./.skills/create-aero-module/SKILL.md`

## Skills

Use the module creation skill when creating a new Aero CMS module:

- Canonical skill: `./.skills/create-aero-module/SKILL.md`
- Codex skill wrapper: `./.agents/skills/create-aero-module/SKILL.md`
- OpenCode skill wrapper: `./.opencode/skills/create-aero-module/SKILL.md`
