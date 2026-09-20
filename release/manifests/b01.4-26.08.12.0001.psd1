@{
    # Copy this file for each release. Never reuse a public build number because
    # every stage refuses to overwrite existing immutable artifacts or receipts.
    SchemaVersion = 1
    Draft = $true
    PublicVersion = 'b01.4-26.08.12.0001'
    ProductVersion = '1.4.1'
    ReleaseLine = 'b01.4'
    MinimumVersion = '1.4.1-build.20260812.1'
    MigrationBaseline = $true
    KeyId = 'idvb-update-2026-01'
    VelopackVersion = '1.2.0'
    # Keep the executable PSD1 file ASCII for Windows PowerShell 5 compatibility.
    # Human-readable UTF-8 notes live in a separate Markdown file.
    ReleaseNotesPath = '..\notes\b01.4-26.08.12.0001.md'
}
