<?php

use Illuminate\Auth\GenericUser;
use Illuminate\Http\Request;
use Zouriel\Workflows\Core\Engine\Views\DefinitionSummary;
use Zouriel\Workflows\Core\Engine\Views\PermissionsSummary;
use Zouriel\Workflows\Core\Engine\Views\TokenView;
use Zouriel\Workflows\Core\WorkflowException;
use Zouriel\Workflows\Laravel\Http\ApiErrors;
use Zouriel\Workflows\Laravel\Http\ApiJson;
use Zouriel\Workflows\Laravel\Http\AuditCsv;
use Zouriel\Workflows\Laravel\Http\AuthActorResolver;
use Zouriel\Workflows\Laravel\Http\RequestBody;

it('writes DateTimeOffset members the way System.Text.Json does', function (string $instant, string $written) {
    expect(ApiJson::dateTimeOffset($instant))->toBe($written);
})->with([
    ['2026-10-19T09:00:00.000Z', '2026-10-19T09:00:00+00:00'],
    ['2026-10-19T09:00:00.500Z', '2026-10-19T09:00:00.5+00:00'],
    ['2026-10-19T09:00:00.123Z', '2026-10-19T09:00:00.123+00:00'],
    ['2026-10-19T09:00:00.120Z', '2026-10-19T09:00:00.12+00:00'],
]);

it('writes views with every member, and leaves null members out only in compact form', function () {
    $token = new TokenView('t1', 'waiting', null, 's1');

    expect(ApiJson::encode(ApiJson::value($token)))->toBe('{"id":"t1","status":"waiting","waiting":null,"stepId":"s1"}')
        ->and(ApiJson::encode(ApiJson::compact($token)))->toBe('{"id":"t1","status":"waiting","stepId":"s1"}')
        ->and(ApiJson::encode(ApiJson::compact((object) ['kept' => null])))->toBe('{"kept":null}')
        ->and(ApiJson::encode(ApiJson::value([1.0, 2.5, (object) []])))->toBe('[1,2.5,{}]');
});

it('writes a permissions summary with its own members first, as .NET\'s derived record', function () {
    $summary = new PermissionsSummary(new DefinitionSummary('d', 'k', 'lifecycle', 'T', null, 'published', 2, false,
        '2026-10-19T09:00:00.000Z', 'admin', 0), true, [], 1);

    expect(array_keys(get_object_vars(ApiJson::value($summary))))->toBe(['changed', 'draftSkipped', 'recordsMoved', 'recordsKept', 'id', 'key', 'kind',
        'title', 'entity', 'status', 'version', 'hasDraftChanges', 'updatedAt', 'updatedBy', 'runningInstances']);
});

it('maps error codes to the statuses of .NET\'s WorkflowApi', function (string $code, int $status) {
    expect(ApiErrors::status($code))->toBe($status)
        ->and(ApiErrors::render(new WorkflowException($code, 'm'))->getStatusCode())->toBe($status);
})->with([
    ['notAllowed', 403], ['segregationOfDuties', 403], ['notFound', 404], ['noRecord', 404], ['definitionNotFound', 404], ['staleSettings', 409],
    ['taskClosed', 409], ['alreadyDecided', 409], ['resetChanged', 409], ['wrongLifecycle', 409], ['notInIncident', 409], ['tooLarge', 413],
    ['invalidSettings', 400], ['lintErrors', 400], ['commentRequired', 400],
]);

it('leaves errors it does not know to the app', function () {
    expect(ApiErrors::render(new RuntimeException('boom')))->toBeNull()
        ->and(ApiErrors::render(new InvalidArgumentException('bad cron'))->getContent())->toBe('{"code":"invalidInput","message":"bad cron"}')
        ->and(ApiErrors::render(new JsonException('x'))->getStatusCode())->toBe(400);
});

it('reads typed bodies like System.Text.Json web defaults', function () {
    $body = RequestBody::required(Request::create('/', 'POST', content: '{"Version":"3","TO":"rev1","flag":true,"data":{},"list":["a"]}'), 1000);

    expect($body->int('version'))->toBe(3)
        ->and($body->string('to'))->toBe('rev1')
        ->and($body->bool('flag'))->toBeTrue()
        ->and($body->object('data'))->toEqual(new stdClass)
        ->and($body->strings('list'))->toBe(['a'])
        ->and($body->string('missing'))->toBeNull();
    expect(fn () => $body->string('flag'))->toThrow(JsonException::class, "'flag' must be text.")
        ->and(fn () => $body->int('to'))->toThrow(JsonException::class)
        ->and(fn () => $body->object('list'))->toThrow(JsonException::class)
        ->and(fn () => RequestBody::required(Request::create('/', 'POST', content: 'null'), 1000))->toThrow(WorkflowException::class, 'The request needs a JSON body.')
        ->and(RequestBody::optional(Request::create('/', 'POST', content: '  '), 1000)->present())->toBeFalse()
        ->and(fn () => RequestBody::optional(Request::create('/', 'POST', content: '"text"'), 1000))->toThrow(JsonException::class)
        ->and(fn () => RequestBody::text(Request::create('/', 'POST', content: str_repeat(' ', 11)), 10))->toThrow(WorkflowException::class);
});

it('quotes CSV cells and defuses ones a spreadsheet would run as formulas', function (?string $value, string $cell) {
    expect(AuditCsv::cell($value))->toBe($cell);
})->with([
    [null, ''],
    ['plain', '"plain"'],
    ['say "hi"', '"say ""hi"""'],
    ['=SUM(A1)', '"\'=SUM(A1)"'],
    ['+1', '"\'+1"'],
    ['-1', '"\'-1"'],
    ['@cmd', '"\'@cmd"'],
    ["\tx", "\"'\tx\""],
]);

it('takes the actor from the signed-in user, with roles from the model or a closure', function () {
    $request = Request::create('/');
    expect((new AuthActorResolver)->resolve($request))->toBeNull();

    $request->setUserResolver(fn () => new class(['id' => 7, 'name' => 'Seven']) extends GenericUser
    {
        public function workflowRoles(): array
        {
            return ['reviewer'];
        }
    });
    $actor = (new AuthActorResolver)->resolve($request);
    expect([$actor->type, $actor->id, $actor->name, $actor->roles])->toBe(['user', '7', 'Seven', ['reviewer']]);

    $custom = (new AuthActorResolver(roles: fn () => ['admin'], name: fn () => 'Custom'))->resolve($request);
    expect([$custom->name, $custom->roles])->toBe(['Custom', ['admin']]);
});
