<?php

namespace Zouriel\Workflows\Tests\Laravel\Http;

/** The workflows the API tests run, on the registry of spec/fixtures/engine (`application` records). */
final class Definitions
{
    /** The shared application lifecycle (spec/fixtures/engine/_lifecycle-application.json) with a reset for admins. */
    public static function lifecycle(): array
    {
        $definition = json_decode(file_get_contents(__DIR__.'/../../../../spec/fixtures/engine/_lifecycle-application.json'), true);
        $definition['lifecycle']['reset'] = ['label' => 'Start over', 'who' => [['role' => 'admin']], 'steps' => [
            ['id' => 'r_notes', 'type' => 'setFields', 'values' => [['field' => 'notes', 'value' => ['const' => 'reset']]]],
        ]];

        return $definition;
    }

    /**
     * A manual sign-off on an application: an approval for rev1 and ali (who may not approve what they started) with
     * a form and remarks required to reject, delegable.
     */
    public static function signOff(): array
    {
        return [
            'specVersion' => 1, 'id' => 'wf_sign', 'key' => 'sign', 'kind' => 'automation', 'title' => 'Sign-off', 'entity' => 'application',
            'trigger' => ['type' => 'manual', 'label' => 'Sign'],
            'steps' => [[
                'id' => 'ap', 'type' => 'approval', 'title' => 'Sign it', 'rule' => ['type' => 'any'], 'due' => 'P2D', 'allowDelegation' => true,
                'requireComment' => 'reject', 'assignees' => [['user' => 'rev1'], ['user' => 'ali']], 'makerChecker' => ['excludeInitiator' => true],
                'form' => ['schemaVersion' => 1, 'id' => 'f_sign', 'title' => 'Sign', 'pages' => [['id' => 'p', 'elements' => [
                    ['kind' => 'field', 'id' => 'f_ref', 'key' => 'reference', 'type' => 'text', 'label' => 'Reference', 'required' => true],
                ]]]],
                'outcomes' => [
                    'approved' => [['id' => 'ok', 'type' => 'setFields', 'values' => [['field' => 'stage', 'value' => ['const' => 'done']]]]],
                    'rejected' => [], 'timedOut' => [],
                ],
            ]],
        ];
    }

    /** Waits for `payment.received` about the record, then copies its amount. */
    public static function waitForPayment(): array
    {
        return [
            'specVersion' => 1, 'id' => 'wf_pay', 'key' => 'waitPay', 'kind' => 'automation', 'title' => 'Wait for payment', 'entity' => 'application',
            'trigger' => ['type' => 'manual', 'label' => 'Await payment'],
            'steps' => [
                ['id' => 'w', 'type' => 'wait', 'until' => ['type' => 'signal', 'signal' => 'payment.received']],
                ['id' => 's', 'type' => 'setFields', 'values' => [['field' => 'amount', 'value' => ['ref' => 'steps.w.data.amount']]]],
            ],
        ];
    }

    /** Pings the CRM (an action the tests make fail) and then marks the record high priority. */
    public static function ping(): array
    {
        return [
            'specVersion' => 1, 'id' => 'wf_ping', 'key' => 'ping', 'kind' => 'automation', 'title' => 'Ping', 'entity' => 'application',
            'trigger' => ['type' => 'manual', 'label' => 'Ping'],
            'steps' => [
                ['id' => 'p', 'type' => 'action', 'action' => 'crm.ping'],
                ['id' => 'end', 'type' => 'setFields', 'values' => [['field' => 'priority', 'value' => ['const' => 'high']]]],
            ],
        ];
    }

    /** A manual automation about no record. */
    public static function standalone(): array
    {
        return [
            'specVersion' => 1, 'id' => 'wf_alone', 'key' => 'alone', 'kind' => 'automation', 'title' => 'Alone',
            'trigger' => ['type' => 'manual', 'label' => 'Run'],
            'steps' => [['id' => 'n', 'type' => 'notify', 'channel' => 'email', 'to' => [['user' => 'admin']], 'body' => 'Ran']],
        ];
    }
}
