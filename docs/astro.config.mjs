import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
// import starlightVersions from 'starlight-versions';

export default defineConfig({
  site: 'https://docs.aerodb.io',
  base: '/',
  integrations: [
    starlight({
      title: 'AeroDB v0.0.8.1-alpha',
      logo: {
        src: './src/assets/logo.png',
        alt: 'AeroDB',
      },
      // plugins: [
      //   starlightVersions({
      //     versions: [{ slug: '0.0.8.1-alpha', label: 'v0.0.8.1 (alpha)' }],
      //   }),
      // ],
      description: 'Fast, multi-model document database for .NET — built on SurrealDB',
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/microbian-systems/AeroDB' },
      ],
      editLink: {
        baseUrl: 'https://github.com/microbian-systems/AeroDB/edit/main/docs/',
      },
      sidebar: [
        {
          label: 'Getting Started',
          items: [
            { label: 'Introduction', slug: '' },
            { label: 'Installation', slug: 'getting-started/installation' },
            { label: 'Quick Start', slug: 'getting-started/quick-start' },
            { label: 'Configuration', slug: 'getting-started/configuration' },
          ],
        },
        {
          label: 'Concepts',
          items: [
            { label: 'Architecture', slug: 'concepts/architecture' },
            { label: 'Documents', slug: 'concepts/documents' },
            { label: 'Schemas', slug: 'concepts/schemas' },
            { label: 'Document Types', slug: 'concepts/document-types' },
            { label: 'Events', slug: 'concepts/events' },
            { label: 'Projections', slug: 'concepts/projections' },
            { label: 'Multi-Tenancy', slug: 'concepts/multi-tenancy' },
          ],
        },
        {
          label: 'Querying',
          items: [
            { label: 'Overview', slug: 'querying' },
            { label: 'Relational', slug: 'relational' },
            { label: 'Graphs', slug: 'graphs' },
            { label: 'Time Series', slug: 'timeseries' },
            { label: 'Searching', slug: 'searching' },
          ],
        },
        {
          label: 'Guides',
          items: [
            { label: 'CRUD Operations', slug: 'guides/crud' },
            { label: 'LINQ', slug: 'guides/linq' },
            { label: 'Raw SurrealQL', slug: 'guides/surrealql' },
            { label: 'Event Sourcing', slug: 'guides/event-sourcing' },
            { label: 'Live Queries', slug: 'guides/live-queries' },
          ],
        },
        {
          label: 'Integrations',
          items: [
            { label: 'ASP.NET Identity', slug: 'integrations/aspnet-identity' },
            { label: 'Entity Framework Core', slug: 'integrations/efcore' },
            { label: 'Wolverine', slug: 'integrations/wolverine' },
            { label: 'Health Checks', slug: 'integrations/health-checks' },
          ],
        },
        {
          label: 'Recipes',
          items: [
            { label: 'Create a Database', slug: 'recipes/create-a-database' },
            { label: 'Run a LINQ Query', slug: 'recipes/run-a-linq-query' },
            { label: 'Create an Index', slug: 'recipes/create-an-index' },
            { label: 'Optimistic Concurrency', slug: 'recipes/implement-optimistic-concurrency' },
            { label: 'Backup and Restore', slug: 'recipes/backup-and-restore' },
            { label: 'Multi-Tenancy', slug: 'recipes/configure-multi-tenancy' },
            { label: 'Register in DI', slug: 'recipes/register-in-di' },
            { label: 'Live Queries', slug: 'recipes/use-live-queries' },
          ],
        },
        {
          label: 'Examples',
          items: [
            { label: 'Overview', slug: 'examples/overview' },
            { label: 'Configuration', slug: 'examples/configuration' },
            { label: 'Querying', slug: 'examples/querying' },
            { label: 'Graph', slug: 'examples/graph' },
            { label: 'Time Series', slug: 'examples/timeseries' },
            { label: 'Live Queries', slug: 'examples/live-queries' },
            { label: 'Event Sourcing', slug: 'examples/event-sourcing' },
            { label: 'Full-Text Search', slug: 'examples/fulltext-search' },
          ],
        },
        {
          label: 'Advanced',
          items: [
            { label: 'Performance', slug: 'advanced/performance' },
            { label: 'Schema Management', slug: 'advanced/schema' },
            { label: 'Transactions', slug: 'advanced/transactions' },
            { label: 'Search', slug: 'advanced/search' },
            { label: 'Custom Serialization', slug: 'advanced/serialization' },
            { label: 'Reactive Streams', slug: 'advanced/reactive-streams' },
            { label: 'Snowflake IDs', slug: 'advanced/snowflake' },
          ],
        },
        {
          label: 'API Reference',
          collapsed: true,
          items: [{ autogenerate: { directory: 'api' } }],
        },
        { label: 'FAQ', slug: 'faq' },
        { label: 'Roadmap', slug: 'roadmap' },
      ],
      customCss: ['./src/styles/custom.css'],
      components: {
        Footer: './src/components/Footer.astro',
      },
    }),
  ],
  outDir: 'dist',
});
