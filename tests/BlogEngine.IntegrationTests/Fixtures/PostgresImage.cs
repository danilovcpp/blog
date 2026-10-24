namespace BlogEngine.IntegrationTests.Fixtures;

public static class PostgresImage
{
    // Retrieved by docker pull postgres:18.6 on 2026-10-24; also pinned in compose.yaml.
    public const string Reference = "postgres:18.6@sha256:edb0c08dc33befd0262ab3b5cf1c4f3f8cfd8b09e88c05b45adee29d6761ddb6";
}
