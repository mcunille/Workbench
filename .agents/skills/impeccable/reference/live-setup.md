# Live integration disabled in Workbench

Impeccable Live is disabled because its inspected-page JavaScript receives a reusable
credential for the local helper's source-reading and editing APIs (security finding #2).
Both repository launchers reject Live commands before resolving or starting an engine.

Use ordinary browser inspection and agent-led source edits for visual iteration.
Do not start a Live server, inject Live scripts, configure Live, or bypass the launcher
with a cached/downloaded engine. Do not weaken CSP to enable the overlay.

This is repository integration containment, not a repair to the external engine.
It does not stop an already-running helper or secure engines launched directly outside
this integration. Re-enabling Live requires a separately reviewed fix that keeps helper
credentials and filesystem authority outside the inspected page's JavaScript realm.
