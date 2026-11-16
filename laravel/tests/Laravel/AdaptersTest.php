<?php

use Illuminate\Contracts\Notifications\Dispatcher;
use Illuminate\Http\Client\Factory;
use Illuminate\Http\Client\Request;
use Illuminate\Support\Facades\Http;
use Illuminate\Support\Facades\Notification;
use Orchestra\Testbench\TestCase;
use Zouriel\Workflows\Core\Host\NotificationRecipient;
use Zouriel\Workflows\Core\Host\WebhookFailedException;
use Zouriel\Workflows\Core\Host\WebhookRequest;
use Zouriel\Workflows\Core\Host\WorkflowNotification;
use Zouriel\Workflows\Core\Registry\ActionDescriptor;
use Zouriel\Workflows\Laravel\Adapters\ConfigSecrets;
use Zouriel\Workflows\Laravel\Adapters\ContainerActions;
use Zouriel\Workflows\Laravel\Adapters\HttpWebhookClient;
use Zouriel\Workflows\Laravel\Adapters\LaravelNotificationSender;
use Zouriel\Workflows\Laravel\Adapters\NotifiableResolver;
use Zouriel\Workflows\Laravel\Adapters\WorkflowMessage;
use Zouriel\Workflows\Laravel\WorkflowsServiceProvider;
use Zouriel\Workflows\Tests\Support\App\StampNotes;

uses(TestCase::class)->beforeEach(function () {
    $this->app->register(WorkflowsServiceProvider::class);
});

function webhook(string $url = 'https://hooks.example.com/in', array $headers = []): WebhookRequest
{
    return new WebhookRequest('POST', $url, $headers, '{"a":1}', 'key-1', 5000);
}

it('sends webhooks with the idempotency key, the body and no redirects', function () {
    Http::fake(['hooks.example.com/*' => Http::response('{"ok":true}', 302)]);
    $client = new HttpWebhookClient($this->app->make(Factory::class), resolve: fn () => ['93.184.216.34']);
    $response = $client->send(webhook(headers: ['X-Sig' => 'abc', 'Content-Type' => 'application/json']));

    expect($response->status)->toBe(302)
        ->and($response->body)->toBe('{"ok":true}');
    Http::assertSent(fn (Request $r) => $r->header('Idempotency-Key') === ['key-1'] && $r->header('X-Sig') === ['abc'] && $r->body() === '{"a":1}'
        && $r->method() === 'POST');
});

it('refuses hosts that resolve only to private or local addresses', function () {
    Http::fake();
    $client = new HttpWebhookClient($this->app->make(Factory::class), resolve: fn () => ['10.0.0.5', '127.0.0.1']);
    expect(fn () => $client->send(webhook()))->toThrow(WebhookFailedException::class, 'private or local');
    expect(fn () => $client->send(webhook('http://169.254.169.254/latest')))->toThrow(WebhookFailedException::class);
    Http::assertNothingSent();

    $allowed = new HttpWebhookClient($this->app->make(Factory::class), allowPrivateNetworks: true, resolve: fn () => ['10.0.0.5']);
    expect($allowed->send(webhook())->status)->toBe(200);
});

it('knows which addresses are private', function (string $address, bool $private) {
    expect(HttpWebhookClient::isPrivate($address))->toBe($private);
})->with([
    ['10.1.2.3', true], ['172.16.0.1', true], ['172.32.0.1', false], ['192.168.1.1', true], ['100.64.0.1', true], ['127.0.0.1', true],
    ['169.254.169.254', true], ['224.0.0.1', true], ['93.184.216.34', false], ['::1', true], ['::', true], ['fe80::1', true], ['fd00::1', true],
    ['::ffff:10.0.0.1', true], ['::ffff:93.184.216.34', false], ['64:ff9b::a00:1', true], ['2002:a00:1::', true], ['2001::1', true],
    ['2001:db8::1', true], ['2606:2800:220:1::1', false], ['not an address', true],
]);

it('refuses headers that could smuggle another header', function () {
    Http::fake();
    $client = new HttpWebhookClient($this->app->make(Factory::class), resolve: fn () => ['93.184.216.34']);
    expect(fn () => $client->send(webhook(headers: ['X-A' => "ok\r\nX-B: evil"])))->toThrow(InvalidArgumentException::class);
    expect(fn () => $client->send(webhook(headers: ['Bad Name' => 'x'])))->toThrow(InvalidArgumentException::class);
    Http::assertNothingSent();
});

it('fails a webhook whose response is too large', function () {
    Http::fake(['*' => Http::response(str_repeat('x', 2000))]);
    $client = new HttpWebhookClient($this->app->make(Factory::class), maxResponseBytes: 1000, resolve: fn () => ['93.184.216.34']);
    expect(fn () => $client->send(webhook()))->toThrow(WebhookFailedException::class, 'larger than 1000');
});

it('delivers a notification once per recipient and idempotency key', function () {
    Notification::fake();
    $resolver = new class implements NotifiableResolver
    {
        public function notifiable(NotificationRecipient $recipient): ?object
        {
            return $recipient->email === null ? null : Notification::route('mail', $recipient->email);
        }
    };
    $sender = new LaravelNotificationSender($this->app->make(Dispatcher::class), $resolver, $this->app->make('cache.store'));
    $notification = new WorkflowNotification('email', [new NotificationRecipient('u1', 'u1@example.com', 'U1'), new NotificationRecipient('u2', null, 'U2')],
        'Review', 'Please review', 'https://app.test/x', 'i1', 'application', '1', 'idem-1');

    $sender->send($notification);
    $sender->send($notification);

    Notification::assertSentOnDemandTimes(WorkflowMessage::class, 1);
    Notification::assertSentOnDemand(WorkflowMessage::class, fn (WorkflowMessage $m, array $channels) => $channels === ['mail'] && $m->notification->subject === 'Review');
});

it('resolves action handlers and secrets from the app', function () {
    config(['workflows.secrets' => ['finance' => 's3cret']]);
    $actions = $this->app->make(ContainerActions::class);

    expect($actions->resolve(new ActionDescriptor('stamp', handler: StampNotes::class)))->toBeInstanceOf(StampNotes::class)
        ->and($actions->resolve(new ActionDescriptor('none', handler: null)))->toBeNull()
        ->and($actions->resolve(new ActionDescriptor('wrong', handler: stdClass::class)))->toBeNull()
        ->and($this->app->make(ConfigSecrets::class)->secret('finance'))->toBe('s3cret')
        ->and($this->app->make(ConfigSecrets::class)->secret('other'))->toBeNull();
});
