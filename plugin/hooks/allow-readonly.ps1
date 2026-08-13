<#
.SYNOPSIS
    PreToolUse hook: auto-allow the 18 read-only LabVIEW MCP tools, prompt for the rest.

.DESCRIPTION
    A plugin cannot ship a permissions allow-list: a plugin's settings.json honours only
    the `agent` and `subagentStatusLine` keys (Claude Code plugins reference, "Settings"),
    and `permissions` is not among them. So the allow-list that lived in
    .claude/settings.json is reimplemented here as a decision hook.

    Claude Code pipes the PreToolUse event as JSON on stdin. We read `tool_name`, and if it
    is one of the 18 passive tools we return an `allow` decision so the read runs without a
    prompt. For anything else — the nine mutating tools and the six `lvai_monitor_*` tools —
    we print nothing and exit 0, which means "no decision; normal permission flow applies",
    so those still prompt.

    Tool names are the PLUGIN-SCOPED names. A tool from a plugin's bundled MCP server is
    named  mcp__plugin_<plugin-name>_<server-name>__<tool>  (Claude Code plugins reference,
    "Plugin-provided MCP servers" / hooks "Match MCP tools"). Here the plugin is
    `labview-mcp` and the server key in .mcp.json is `labview`, so the prefix is
    mcp__plugin_labview-mcp_labview__ .

    Why exactly these 18: they are the tools carrying `readOnlyHint`, minus the six
    `lvai_monitor_*` tools. The monitors are "read-only" only in that they wait — but they
    block for up to `timeoutSeconds` and their `replyJson` argument writes content back into
    LabVIEW's UI, so they are worth a prompt. The server is deliberately NOT allow-listed
    wholesale: that would wave through `lvai_run_vi_as_top_level` and `lvai_apply_aixml_to_vi`.
#>
$ErrorActionPreference = 'Stop'

$prefix = 'mcp__plugin_labview-mcp_labview__'
$allow = @(
    'lvai_status',
    'lvai_dump_schema',
    'lvai_get_application_configuration',
    'lvai_describe_vi',
    'lvai_describe_project',
    'lvai_search_info_cache',
    'lvai_lookup_info_cache_items',
    'lvai_filter_palette_search_candidates',
    'lvai_filter_example_search_candidates',
    'lvai_convert_vi_to_aixml',
    'lvai_validate_aixml',
    'lvai_aixml_reference',
    'lvai_dqmh_reference',
    'lvai_lvproj_reference',
    'lvai_list_labview_installations',
    'lvai_lvlib_reference',
    'lvai_vi_server_reference',
    'lvai_palette_index'
) | ForEach-Object { $prefix + $_ }

# Read the whole event off stdin. If there is nothing, or it does not parse, stay silent
# (exit 0, no output) so the normal permission flow decides — never fail a tool call.
$raw = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

try {
    $event = $raw | ConvertFrom-Json
} catch {
    exit 0
}

$tool = $event.tool_name
if ($allow -contains $tool) {
    $decision = @{
        hookSpecificOutput = @{
            hookEventName      = 'PreToolUse'
            permissionDecision = 'allow'
        }
    }
    # -Compress keeps it to one line; -Depth covers the nested object.
    [Console]::Out.Write(($decision | ConvertTo-Json -Compress -Depth 5))
}

exit 0
