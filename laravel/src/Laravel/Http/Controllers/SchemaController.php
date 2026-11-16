<?php

namespace Zouriel\Workflows\Laravel\Http\Controllers;

use Illuminate\Http\Request;
use Illuminate\Http\Response;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Registry\Schema;
use Zouriel\Workflows\Core\Settings\GlobalSettings;
use Zouriel\Workflows\Laravel\Http\ApiJson;
use Zouriel\Workflows\Laravel\Http\ApiOptions;
use Zouriel\Workflows\Laravel\Http\Middleware\AuthorizeOperation;

/** `GET /schema`, `/settings`, `/triggers`, `/directory/users`, `/me`. */
final class SchemaController extends Controller
{
    public function schema(): Response
    {
        return ApiJson::response(Schema::response($this->engine->registry));
    }

    /** The global settings and their revision (they name webhook connections and secrets: editors only). */
    public function settings(): Response
    {
        [$settings, $revision] = $this->engine->getSettings();

        return ApiJson::response((object) ['settings' => $settings->toJson(), 'revision' => $revision]);
    }

    public function saveSettings(Request $request): Response
    {
        $body = $this->body($request);
        $settings = $body->value('settings');
        $revision = $this->engine->saveSettings(GlobalSettings::parse($settings === null ? null : Values::serialize($settings)), $body->int('revision') ?? 0,
            $this->actor($request));

        return ApiJson::response((object) ['revision' => $revision]);
    }

    public function triggers(): Response
    {
        return ApiJson::response($this->engine->listTriggers());
    }

    public function users(Request $request): Response
    {
        return ApiJson::response($this->engine->options->identity->searchUsers(self::queryText($request, 'q'),
            self::clamp(self::queryInt($request, 'take'), 20, 1, 100)));
    }

    /** One person, or an empty 404 (as .NET's `Results.NotFound()`). */
    public function user(string $id): Response
    {
        $user = $this->engine->options->identity->user($id);

        return $user === null ? new Response('', 404) : ApiJson::response($user);
    }

    /** Who is asking and what they may do, so a UI can hide or lock what the server would refuse. */
    public function me(Request $request): Response
    {
        $me = $this->actor($request);
        $options = $this->options();
        $can = fn (string $kind) => AuthorizeOperation::allows($request, $options, $kind);

        return ApiJson::response((object) [
            'type' => $me->type, 'id' => $me->id, 'name' => $me->name, 'roles' => $me->roles ?? [],
            'can' => (object) [
                'edit' => $can(ApiOptions::EDIT), 'publish' => $can(ApiOptions::PUBLISH), 'settings' => $can(ApiOptions::SETTINGS),
                'intervene' => $can(ApiOptions::INTERVENE), 'audit' => $can(ApiOptions::AUDIT),
            ],
        ]);
    }
}
