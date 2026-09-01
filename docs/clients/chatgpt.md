# ChatGPT and the OpenAI API

**This server cannot be connected to ChatGPT as it stands.** That is a transport limitation,
not a missing feature.

## Why

MCP defines several transports. This server speaks **stdio**: the client launches it as a
child process and exchanges JSON-RPC over stdin and stdout. That requires client and server
to be on the same machine.

OpenAI's MCP support is for **remote servers reached over HTTP**. There is no mechanism for
ChatGPT to spawn a local process on your computer, so a stdio server is out of reach.

## What it would take

The C# MCP SDK can host an HTTP transport from the same tool code, so the transport work is
mostly additive rather than a rewrite. The transport is not the hard part.

Over stdio, the security boundary is "whoever can run this process" - normally just you. Over
HTTP it becomes "whoever can reach this URL", and these tools include `drop_table`,
`create_role` and `grant_privileges`. Before exposing anything you would need authentication,
network restrictions, and a database role that cannot do damage.

## Recommendation

Do not put this server on the network as it stands. If you want ChatGPT to reach your
database, build a separate, deliberately narrow HTTP service that exposes only `query`,
backed by a read-only role - see [security.md](../security.md). A smaller attack surface is
worth more than reusing this code.

## Checking current support

OpenAI's MCP surface is moving quickly. Verify the current transport requirements and
authentication model against OpenAI's own documentation before building anything.
