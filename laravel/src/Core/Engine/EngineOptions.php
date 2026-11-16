<?php

namespace Zouriel\Workflows\Core\Engine;

use Closure;
use Zouriel\Workflows\Core\Host\ActionResolver;
use Zouriel\Workflows\Core\Host\ActorAccessor;
use Zouriel\Workflows\Core\Host\FormsValidator;
use Zouriel\Workflows\Core\Host\FormValidator;
use Zouriel\Workflows\Core\Host\NoActions;
use Zouriel\Workflows\Core\Host\NoIdentity;
use Zouriel\Workflows\Core\Host\NoNotifications;
use Zouriel\Workflows\Core\Host\NoSecrets;
use Zouriel\Workflows\Core\Host\NotificationSender;
use Zouriel\Workflows\Core\Host\SecretResolver;
use Zouriel\Workflows\Core\Host\WebhookClient;
use Zouriel\Workflows\Core\Host\WorkflowIdentity;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Storage\WorkflowStore;
use Zouriel\Workflows\Core\Time\Clock;
use Zouriel\Workflows\Core\Time\SystemClock;

/** Everything the engine needs from the host app. Port of .NET `Engine/WorkflowEngineOptions.cs`. */
final class EngineOptions
{
    public WorkflowIdentity $identity;

    public NotificationSender $notifications;

    public SecretResolver $secrets;

    public ActionResolver $actions;

    public Clock $clock;

    public FormValidator $formValidator;

    /**
     * @param  WebhookClient|null  $webhooks  null refuses every webhook (the Laravel layer supplies an HTTP client)
     * @param  int  $maxStepsPerRun  safety stop: the most steps one instance may run before it waits
     * @param  string|null  $taskNotificationChannel  null = the first of inApp/email the registry lists
     * @param  string|null  $recordLinkTemplate  e.g. `https://app.example.com/records/{entity}/{id}`
     * @param  int  $ownershipRefreshMs  how long the change hook trusts its list of lifecycle-owned fields
     * @param  (Closure(string): void)|null  $warn  where problems the engine works around are reported
     */
    public function __construct(
        public WorkflowRegistry $registry,
        public WorkflowStore $store,
        ?WorkflowIdentity $identity = null,
        ?NotificationSender $notifications = null,
        public ?WebhookClient $webhooks = null,
        ?SecretResolver $secrets = null,
        ?ActionResolver $actions = null,
        ?Clock $clock = null,
        public ?string $tenant = null,
        public int $maxStepsPerRun = 5000,
        public ?string $taskNotificationChannel = null,
        public bool $notifyTaskAssignees = true,
        ?FormValidator $formValidator = null,
        public ?ActorAccessor $actorAccessor = null,
        public ?SimulationLog $simulationLog = null,
        public ?string $recordLinkTemplate = null,
        public int $ownershipRefreshMs = 30000,
        public ?Closure $warn = null,
    ) {
        $this->identity = $identity ?? new NoIdentity;
        $this->notifications = $notifications ?? new NoNotifications;
        $this->secrets = $secrets ?? new NoSecrets;
        $this->actions = $actions ?? new NoActions;
        $this->clock = $clock ?? new SystemClock;
        $this->formValidator = $formValidator ?? new FormsValidator;
    }
}
