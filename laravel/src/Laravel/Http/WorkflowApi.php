<?php

namespace Zouriel\Workflows\Laravel\Http;

use Illuminate\Contracts\Container\Container;
use Illuminate\Support\Facades\Route;
use Zouriel\Workflows\Laravel\Http\Controllers\AuditController;
use Zouriel\Workflows\Laravel\Http\Controllers\DefinitionController;
use Zouriel\Workflows\Laravel\Http\Controllers\InstanceController;
use Zouriel\Workflows\Laravel\Http\Controllers\RecordController;
use Zouriel\Workflows\Laravel\Http\Controllers\SchemaController;
use Zouriel\Workflows\Laravel\Http\Controllers\TaskController;
use Zouriel\Workflows\Laravel\Http\Middleware\AuthorizeOperation;
use Zouriel\Workflows\Laravel\Http\Middleware\IdentifyActor;

/**
 * The workflow REST API (spec/api/openapi.json), the one the @zouriel/ui workflow components call. The service provider
 * mounts it at `config('workflows.path')` with `config('workflows.api.middleware')`; set `workflows.api.routes` to false
 * and call {@see self::routes()} to mount it yourself. Port of .NET's `MapWorkflowApi`.
 */
final class WorkflowApi
{
    /** The defaults the API needs from the container; an app binding its own keeps it. */
    public static function bindDefaults(Container $app): void
    {
        $app->bindIf(ActorResolver::class, AuthActorResolver::class, true);
        $app->bindIf(AccessPolicy::class, AllowAllAccessPolicy::class, true);
    }

    /**
     * Registers every endpoint under `$prefix` (default `config('workflows.path')`) behind `$middleware` (default
     * `config('workflows.api.middleware')`), then the caller check and each operation's gate.
     *
     * @param  list<string>|null  $middleware
     */
    public static function routes(?string $prefix = null, ?array $middleware = null): void
    {
        Route::prefix($prefix ?? config('workflows.path', 'api/workflows'))
            ->middleware([...($middleware ?? config('workflows.api.middleware', ['api'])), IdentifyActor::class])
            ->group(function () {
                $gate = fn (string $kind) => AuthorizeOperation::class.':'.$kind;
                $view = $gate(ApiOptions::VIEW);
                $edit = $gate(ApiOptions::EDIT);
                $publish = $gate(ApiOptions::PUBLISH);
                $intervene = $gate(ApiOptions::INTERVENE);
                $audit = $gate(ApiOptions::AUDIT);
                $settings = $gate(ApiOptions::SETTINGS);

                Route::get('schema', [SchemaController::class, 'schema'])->middleware($view);
                Route::get('settings', [SchemaController::class, 'settings'])->middleware($edit);
                Route::put('settings', [SchemaController::class, 'saveSettings'])->middleware($settings);
                Route::get('triggers', [SchemaController::class, 'triggers'])->middleware($view);
                Route::get('directory/users', [SchemaController::class, 'users'])->middleware($view);
                Route::get('directory/users/{id}', [SchemaController::class, 'user'])->middleware($view);
                Route::get('me', [SchemaController::class, 'me'])->middleware($view);

                Route::get('definitions', [DefinitionController::class, 'index'])->middleware($view);
                Route::post('definitions', [DefinitionController::class, 'store'])->middleware($edit);
                Route::get('definitions/{id}', [DefinitionController::class, 'show'])->middleware($view);
                Route::put('definitions/{id}', [DefinitionController::class, 'update'])->middleware($edit);
                Route::delete('definitions/{id}', [DefinitionController::class, 'destroy'])->middleware($publish);
                Route::post('definitions/{id}/publish', [DefinitionController::class, 'publish'])->middleware($publish);
                Route::post('definitions/{id}/permissions', [DefinitionController::class, 'permissions'])->middleware($publish);
                Route::post('definitions/{id}/archive', [DefinitionController::class, 'archive'])->middleware($publish);
                Route::post('definitions/{id}/restore', [DefinitionController::class, 'restore'])->middleware($publish);
                Route::post('definitions/{id}/rollback', [DefinitionController::class, 'rollback'])->middleware($publish);
                Route::get('definitions/{id}/versions/{version}', [DefinitionController::class, 'version'])->middleware($view)->where('version', '-?[0-9]{1,9}');
                Route::get('definitions/{id}/diff', [DefinitionController::class, 'diff'])->middleware($view);
                Route::post('validate', [DefinitionController::class, 'validate'])->middleware($edit);
                Route::post('simulate', [DefinitionController::class, 'simulate'])->middleware($edit);

                Route::get('instances', [InstanceController::class, 'index'])->middleware($view);
                Route::get('instances/{id}', [InstanceController::class, 'show'])->middleware($view);
                Route::get('instances/{id}/definition', [InstanceController::class, 'definition'])->middleware($view);
                Route::get('instances/{id}/audit', [InstanceController::class, 'audit'])->middleware($audit);
                Route::post('instances/{id}/cancel', [InstanceController::class, 'cancel'])->middleware($intervene);
                Route::post('instances/{id}/retry', [InstanceController::class, 'retry'])->middleware($intervene);
                Route::post('instances/{id}/skip', [InstanceController::class, 'skip'])->middleware($intervene);
                Route::post('instances/{id}/migrate', [InstanceController::class, 'migrate'])->middleware($intervene);

                Route::get('tasks', [TaskController::class, 'index'])->middleware($view);
                Route::get('tasks/{id}', [TaskController::class, 'show'])->middleware($view);
                Route::post('tasks/{id}/decide', [TaskController::class, 'decide'])->middleware($view);
                Route::post('tasks/{id}/delegate', [TaskController::class, 'delegate'])->middleware($view);
                Route::post('tasks/{id}/reassign', [TaskController::class, 'reassign'])->middleware($intervene);

                Route::get('records/{entity}/{id}', [RecordController::class, 'show'])->middleware($view);
                Route::get('records/{entity}/{id}/history', [RecordController::class, 'history'])->middleware($view);
                Route::post('records/{entity}/{id}/transitions/{key}', [RecordController::class, 'transition'])->middleware($view);
                Route::get('records/{entity}/{id}/reset', [RecordController::class, 'resetPreview'])->middleware($view);
                Route::post('records/{entity}/{id}/reset', [RecordController::class, 'reset'])->middleware($view);
                Route::post('records/{entity}/{id}/force-state', [RecordController::class, 'forceState'])->middleware($intervene);
                Route::post('records/{entity}/{id}/manual/{workflow}', [RecordController::class, 'manual'])->middleware($view);
                Route::post('manual/{workflow}', [RecordController::class, 'manualWithoutRecord'])->middleware($view);
                Route::post('signals/{key}', [RecordController::class, 'signal'])->middleware($gate(ApiOptions::SIGNAL));

                Route::get('audit', [AuditController::class, 'index'])->middleware($audit);
                Route::get('audit/export', [AuditController::class, 'export'])->middleware($gate(ApiOptions::AUDIT_EXPORT));
                Route::post('audit/verify', [AuditController::class, 'verify'])->middleware($audit);
                Route::post('audit/purge', [AuditController::class, 'purge'])->middleware($settings);
            });
    }
}
