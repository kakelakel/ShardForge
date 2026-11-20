# AI assistants

The world editor's **Ask** panel and Walk's **Ask AI** window can use an AI assistant: **ChatGPT** (OpenAI's API),
**Claude** (Anthropic's API) or a **local model** through Ollama. ShardForge Server makes the requests, so
everyone on your server can use them from any browser, and the API keys never leave the server.

## Setting it up

Admin → **AI assistants**:

1. Turn on the one you want, paste its API key, and press **Test**.
   - ChatGPT: a key from platform.openai.com (API keys). The default model is `gpt-5.6-sol`; type another model
     name to change it. The address can point at any service that speaks OpenAI's API (Azure OpenAI,
     OpenRouter, LM Studio).
   - Claude: a key from console.anthropic.com. Default model `claude-sonnet-5-5`.
   - Ollama: install Ollama, `ollama pull llama3.1` (or another model), turn it on. No key; set the address if
     it runs on another machine.
2. Set each level's daily allowance under **Requests per person per day, by level**: a number of requests, 0 to
   turn the assistant off for that level, or **No limit**. Defaults: Decorator 30, Worldbuilder 100, Admin no
   limit. Viewers only look, so they never get it. The editor and Walk show each person how many they have left
   today; counts reset at midnight (UTC).

API keys are billed by OpenAI and Anthropic per use; a ChatGPT or Claude subscription doesn't include API
access. Keys are stored in `data/ai.json` on the server (not in backups) and are never sent to a browser: the
Admin page shows only their last four characters.

With more than one assistant on, the Ask panel and the Ask AI window get a picker.

## How it works

The assistant never touches the map itself. It gets a description and a list of tools, and answers with a plan:

- **Editor**: your request, a summary of the map (size, terrain, heights, towns) and the editor's operations
  (`world`, `mountain`, `path`, `town`, `populate_world`, `underground`...). The plan runs through the same
  functions as your own tools; Undo reverts all of it. Worldbuilders and admins.
- **Walk**: your request, the area round you (or the plot you stand on): the ground and its heights, every static
  by name, id and height, server items, the house designs, and the catalogue of statics by name and id. The plan
  places, fills, lines up, removes, builds houses, and puts down spawners, signs and working items. It runs as one
  step: Undo takes it all back, and everyone on the map sees it live. Decorators and up (what they may change is
  checked as usual).

Steps the assistant gets wrong (a static that doesn't exist, a spot outside the area) are skipped and listed.

Every request is in **Activity** (who asked which assistant what, where), with the model and how long it took.

## The API

| | |
|---|---|
| `GET /api/ai` | the assistants that are ready, and your allowance today `{ limit, used, left }` (-1 = no limit) (Viewer) |
| `POST /api/ai/ask` | `{ provider, prompt, request?, where? }` → `{ text, model, ms }` (Decorator) |
| `GET/PUT /api/ai/settings` | `{ provider, enabled?, key?, model?, url?, limits?: { Decorator, Worldbuilder, Admin } }`; key `"-"` removes it; a limit of 0 is off, -1 no limit (Admin) |
| `POST /api/ai/test` | `{ provider }`: a quick check that it answers (Admin) |
