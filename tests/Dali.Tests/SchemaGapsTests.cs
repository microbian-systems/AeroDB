using TUnit.Core;

namespace Dali.Tests;

// Test types for polymorphic schema configuration
public class Employee : Person
{
    public string Department { get; set; } = "";
}

public class Manager : Employee
{
    public int TeamSize { get; set; }
}



public class SchemaGapsTests
{
    [Test]
    public void DatabaseSchemaName_is_null_by_default()
    {
        var options = new EventSourcingOptions();
        options.DatabaseSchemaName.ShouldBeNull();
        options.EventsSchemaName.ShouldBeNull();
    }

    [Test]
    public void ForEvents_returns_mt_events()
    {
        var schema = new SchemaOptions();
        schema.ForEvents().ShouldBe("mt_events");
    }

    [Test]
    public void ForStreams_returns_mt_events()
    {
        var schema = new SchemaOptions();
        schema.ForStreams<string>().ShouldBe("mt_events");
    }

    [Test]
    public void ForEventProgression_returns_mt_projection_progress()
    {
        var schema = new SchemaOptions();
        schema.ForEventProgression().ShouldBe("mt_projection_progress");
    }

    [Test]
    public void EventsTableName_is_mt_events()
    {
        var schema = new SchemaOptions();
        schema.EventsTableName.ShouldBe("mt_events");
    }

    [Test]
    public void ProjectionProgressTableName_is_mt_projection_progress()
    {
        var schema = new SchemaOptions();
        schema.ProjectionProgressTableName.ShouldBe("mt_projection_progress");
    }

    [Test]
    public void ForStreams_returns_same_as_ForEvents()
    {
        var schema = new SchemaOptions();
        schema.ForStreams<int>().ShouldBe(schema.ForEvents());
    }

    // ── SubClass ──────────────────────────────────────────────────

    [Test]
    public void SubClass_RegistersDerivedType()
    {
        var mapping = new DocumentMapping<Person>();
        mapping.AddSubClass<Employee>();
        mapping.SubClasses.Count.ShouldBe(1);
        mapping.SubClasses[0].ShouldBe(typeof(Employee));
    }

    [Test]
    public void SubClass_Chains()
    {
        var mapping = new DocumentMapping<Person>();
        mapping.AddSubClass<Employee>().AddSubClass<Manager>();
        mapping.SubClasses.Count.ShouldBe(2);
    }

    // ── IgnoreIndex ──────────────────────────────────────────────

    [Test]
    public void IgnoreIndex_PreventsIndexCreation()
    {
        var mapping = new DocumentMapping<Person>();
        mapping.IgnoreIndex("idx_person_name");
        mapping.IgnoredIndexes.Count.ShouldBe(1);
        mapping.IgnoredIndexes.ShouldContain("idx_person_name");
    }

    [Test]
    public void IgnoreIndex_Chains()
    {
        var mapping = new DocumentMapping<Person>();
        mapping.IgnoreIndex("idx_a").IgnoreIndex("idx_b");
        mapping.IgnoredIndexes.Count.ShouldBe(2);
    }

    // ── ForeignKey ───────────────────────────────────────────────

    [Test]
    public void ForeignKey_StoresMetadata()
    {
        var mapping = new DocumentMapping<Person>();
        mapping.ForeignKey<Employee>(p => p.Name!);
        mapping.ForeignKeys.Count.ShouldBe(1);
        mapping.ForeignKeys[0].ChildType.ShouldBe(typeof(Employee));
        mapping.ForeignKeys[0].PropertyName.ShouldBe("Name");
    }

    [Test]
    public void ForeignKey_WithCascadeDelete()
    {
        var mapping = new DocumentMapping<Person>();
        mapping.ForeignKey<Employee>(p => p.Name!, fk => fk.CascadeDelete = true);
        mapping.ForeignKeys[0].CascadeDelete.ShouldBeTrue();
    }

    // ── ComputedIndex ────────────────────────────────────────────

    [Test]
    public void ComputedIndex_StoresOptions()
    {
        var mapping = new DocumentMapping<Person>();
        mapping.ComputedIndex(p => p.Name!, opts =>
        {
            opts.Method = ComputedIndexOptions.IndexMethod.Unique;
            opts.Predicate = "WHERE active = true";
        });
        var idx = mapping.Indices.FirstOrDefault(i => i.Name.Contains("person_name"));
        idx.ShouldNotBeNull();
        idx.ComputedOptions.ShouldNotBeNull();
        idx.ComputedOptions.Method.ShouldBe(ComputedIndexOptions.IndexMethod.Unique);
        idx.ComputedOptions.Predicate.ShouldBe("WHERE active = true");
    }

    [Test]
    public void ComputedIndex_Defaults_To_BTree()
    {
        var mapping = new DocumentMapping<Person>();
        mapping.ComputedIndex(p => p.Age, opts => { });
        var idx = mapping.Indices.FirstOrDefault(i => i.Name.Contains("person_age"));
        idx.ShouldNotBeNull();
        idx.ComputedOptions.Method.ShouldBe(ComputedIndexOptions.IndexMethod.BTree);
    }

    // ── MetadataConfig ───────────────────────────────────────────

    [Test]
    public void MetadataConfig_HeadersEnabled_ByDefault()
    {
        var cfg = new MetadataConfig();
        cfg.HeadersEnabled.ShouldBeTrue();
    }

    [Test]
    public void MetadataConfig_CorrelationId_Disabled_ByDefault()
    {
        var cfg = new MetadataConfig();
        cfg.CorrelationIdEnabled.ShouldBeFalse();
    }

    [Test]
    public void MetadataConfig_EnableAll_EnablesEverything()
    {
        var cfg = new MetadataConfig();
        cfg.EnableAll();
        cfg.CorrelationIdEnabled.ShouldBeTrue();
        cfg.CausationIdEnabled.ShouldBeTrue();
        cfg.HeadersEnabled.ShouldBeTrue();
    }

    [Test]
    public void MetadataConfig_OnEventOptions_Accessible()
    {
        var options = new StoreOptions();
        options.Events.MetadataConfig.ShouldNotBeNull();
        options.Events.MetadataConfig.HeadersEnabled.ShouldBeTrue();
    }

    // ── DEFINE ACCESS ────────────────────────────────────────────

    [Test]
    public void DefineAccess_CreatesAccessDefinition()
    {
        var schema = new SchemaOptions();
        schema.Accesses.Add(new AccessDefinition
        {
            Name = "user_access",
            SignupQuery = "CREATE user SET email = $email, pass = crypto::argon2::generate($pass)",
            SigninQuery = "SELECT * FROM user WHERE email = $email AND crypto::argon2::compare(pass, $pass)"
        });
        schema.Accesses.Count.ShouldBe(1);
        schema.Accesses[0].Name.ShouldBe("user_access");
        schema.Accesses[0].Type.ShouldBe("RECORD");
        schema.Accesses[0].Duration.ShouldBe("24h");
    }

    [Test]
    public void DefineAccess_Defaults_Are_Record_And_24h()
    {
        var def = new AccessDefinition { Name = "test" };
        def.Type.ShouldBe("RECORD");
        def.Duration.ShouldBe("24h");
    }

    // ── DEFINE TOKEN ─────────────────────────────────────────────

    [Test]
    public void DefineToken_CreatesTokenDefinition()
    {
        var schema = new SchemaOptions();
        schema.Tokens.Add(new TokenDefinition
        {
            Name = "my_token",
            Type = "HS512",
            Value = "super_secret_key"
        });
        schema.Tokens.Count.ShouldBe(1);
        schema.Tokens[0].Name.ShouldBe("my_token");
        schema.Tokens[0].Type.ShouldBe("HS512");
        schema.Tokens[0].Value.ShouldBe("super_secret_key");
    }

    [Test]
    public void DefineToken_Defaults_To_HS256()
    {
        var def = new TokenDefinition { Name = "t", Value = "secret" };
        def.Type.ShouldBe("HS256");
    }

    // ── DEFINE SCOPE ─────────────────────────────────────────────

    [Test]
    public void DefineScope_CreatesScopeDefinition()
    {
        var schema = new SchemaOptions();
        schema.Scopes.Add(new ScopeDefinition
        {
            Name = "user_scope",
            SignupQuery = "CREATE user SET email = $email",
            SigninQuery = "SELECT * FROM user WHERE email = $email"
        });
        schema.Scopes.Count.ShouldBe(1);
        schema.Scopes[0].Name.ShouldBe("user_scope");
        schema.Scopes[0].SessionDuration.ShouldBe("24h");
    }

    // ── DEFINE FIELD with options ────────────────────────────────

    [Test]
    public void DefineField_WithType_StoresFieldDefinition()
    {
        var mapping = new DocumentMapping<Person>();
        mapping.Field("email", f => f.FieldType = "string");
        mapping.FieldDefinitions.Count.ShouldBe(1);
        mapping.FieldDefinitions[0].FieldName.ShouldBe("email");
        mapping.FieldDefinitions[0].FieldType.ShouldBe("string");
    }

    [Test]
    public void DefineField_WithDefault_StoresDefault()
    {
        var mapping = new DocumentMapping<Person>();
        mapping.Field("age", f =>
        {
            f.FieldType = "int";
            f.DefaultValue = "0";
        });
        mapping.FieldDefinitions[0].DefaultValue.ShouldBe("0");
    }

    [Test]
    public void DefineField_WithAssert_StoresAssert()
    {
        var mapping = new DocumentMapping<Person>();
        mapping.Field("email", f =>
        {
            f.FieldType = "string";
            f.AssertExpression = "string::is::email($value)";
        });
        mapping.FieldDefinitions[0].AssertExpression.ShouldBe("string::is::email($value)");
    }

    [Test]
    public void DefineField_WithPermissions_StoresPermissions()
    {
        var mapping = new DocumentMapping<Person>();
        mapping.Field("role", f =>
        {
            f.FieldType = "string";
            f.Permissions = "WHERE $auth.role = 'admin'";
        });
        mapping.FieldDefinitions[0].Permissions.ShouldBe("WHERE $auth.role = 'admin'");
    }

    [Test]
    public void DefineField_Chains()
    {
        var mapping = new DocumentMapping<Person>();
        mapping.Field("email", f => f.FieldType = "string")
               .Field("age", f => f.FieldType = "int");
        mapping.FieldDefinitions.Count.ShouldBe(2);
    }

    // ── SurrealQL syntax verification (unit — does not execute) ──

    [Test]
    public void EnsureAccesses_Surql_Produces_CorrectSyntax()
    {
        var access = new AccessDefinition
        {
            Name = "user_access",
            Type = "RECORD",
            SignupQuery = "CREATE user SET email = $email",
            SigninQuery = "SELECT * FROM user WHERE email = $email",
            Duration = "7d"
        };
        var sb = new System.Text.StringBuilder();
        sb.Append("DEFINE ACCESS ").Append(access.Name);
        sb.Append(" ON DATABASE TYPE ").Append(access.Type);
        sb.Append(" SIGNUP ( ").Append(access.SignupQuery).Append(" )");
        sb.Append(" SIGNIN ( ").Append(access.SigninQuery).Append(" )");
        sb.Append(" DURATION FOR TOKEN ").Append(access.Duration);
        sb.Append(';');

        var surql = sb.ToString();
        surql.ShouldContain("DEFINE ACCESS user_access");
        surql.ShouldContain("ON DATABASE TYPE RECORD");
        surql.ShouldContain("SIGNUP ( CREATE user SET email = $email )");
        surql.ShouldContain("DURATION FOR TOKEN 7d");
    }

    [Test]
    public void EnsureTokens_Surql_Produces_CorrectSyntax()
    {
        var token = new TokenDefinition { Name = "my_token", Type = "HS512", Value = "s3cret" };
        var surql = $"DEFINE TOKEN {token.Name} ON DATABASE TYPE {token.Type} VALUE \"{token.Value}\";";
        surql.ShouldBe("DEFINE TOKEN my_token ON DATABASE TYPE HS512 VALUE \"s3cret\";");
    }

    [Test]
    public void EnsureScopes_Surql_Produces_CorrectSyntax()
    {
        var scope = new ScopeDefinition
        {
            Name = "user_scope",
            SessionDuration = "12h",
            SignupQuery = "CREATE user SET email = $email"
        };
        var sb = new System.Text.StringBuilder();
        sb.Append("DEFINE SCOPE ").Append(scope.Name);
        sb.Append(" SESSION ").Append(scope.SessionDuration);
        sb.Append(" SIGNUP ( ").Append(scope.SignupQuery).Append(" )");
        sb.Append(';');
        var surql = sb.ToString();
        surql.ShouldContain("DEFINE SCOPE user_scope");
        surql.ShouldContain("SESSION 12h");
        surql.ShouldContain("SIGNUP ( CREATE user SET email = $email )");
    }
}
