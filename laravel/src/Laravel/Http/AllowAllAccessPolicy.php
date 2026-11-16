<?php

namespace Zouriel\Workflows\Laravel\Http;

use Zouriel\Workflows\Core\Host\WorkflowActor;

/** The default access policy: every signed-in caller may see every record (route gates still apply). */
final class AllowAllAccessPolicy extends RecordAccessPolicy
{
    public function canViewRecord(WorkflowActor $actor, string $entity, string $id): bool
    {
        return true;
    }
}
