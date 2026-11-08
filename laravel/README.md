# zouriel/workflows (Laravel)

Status: **in progress** on branch `laravel-engine`. Ported so far: canonical JSON (RFC 8785), the audit hash
chain and its verifier, workflow conditions (through `zouriel/forms`), and the service provider. The model,
lint, engine, Eloquent storage, change capture, REST API and worker follow the phases in
`../docs/plans/go-and-laravel.md`. `composer test` lists every shared fixture not passed yet as a todo.

Install in an app. This folder is published as the private, read-only split repo
`Zouriel/workflows-laravel` (from this branch, with `git subtree split --prefix=laravel`):

```json
"repositories": [
  { "type": "vcs", "url": "git@github.com:Zouriel/zouriel-forms.git" },
  { "type": "vcs", "url": "git@github.com:Zouriel/workflows-laravel.git" }
],
"require": { "zouriel/workflows": "dev-main" }
```

Never commit to the split repo; change `laravel/` here and split again. Conformance tests need `../spec`,
so they only run here (they skip in the split repo).
