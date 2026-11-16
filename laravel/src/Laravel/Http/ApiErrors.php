<?php

namespace Zouriel\Workflows\Laravel\Http;

use Illuminate\Http\Response;
use InvalidArgumentException;
use JsonException;
use stdClass;
use Throwable;
use Zouriel\Workflows\Core\WorkflowException;

/**
 * Turns engine errors and bad input into the standard error body `{ code, message, details }` (spec/api/openapi.json
 * `WorkflowError`) with the status table of .NET's `WorkflowApi.Problem`. `details` is left out when there are none,
 * as .NET writes it.
 */
final class ApiErrors
{
    private const array CONFLICT = ['notInIncident', 'alreadyRunning', 'alreadyAssigned', 'staleSettings', 'taskClosed', 'alreadyDecided', 'duplicateId',
        'duplicateKey', 'finished', 'published', 'keyLocked', 'kindLocked', 'nothingToReset', 'notPublished', 'resetChanged', 'wrongLifecycle'];

    /** The response for an error the API reports, or null for one it does not (it then goes to the app's handler). */
    public static function render(Throwable $e): ?Response
    {
        return match (true) {
            $e instanceof WorkflowException => self::error($e->errorCode, $e->getMessage(), self::status($e->errorCode), $e->details),
            // Bodies that are not JSON, or not the JSON an endpoint takes (a definition that is not one, a member of the wrong type).
            $e instanceof JsonException => self::error('invalidJson', "The request body is not valid JSON for this endpoint: {$e->getMessage()}", 400),
            // Values the request carried that the engine could not use (e.g. a cron field out of range).
            $e instanceof InvalidArgumentException => self::error('invalidInput', $e->getMessage(), 400),
            default => null,
        };
    }

    public static function status(string $code): int
    {
        return match (true) {
            $code === 'notAllowed', $code === 'segregationOfDuties' => 403,
            $code === 'notFound', $code === 'noRecord', $code === 'definitionNotFound' => 404,
            in_array($code, self::CONFLICT, true) => 409,
            $code === 'tooLarge' => 413,
            default => 400,
        };
    }

    public static function error(string $code, string $message, int $status, mixed $details = null): Response
    {
        $body = new stdClass;
        $body->code = $code;
        $body->message = $message;
        if ($details !== null) {
            $body->details = ApiJson::compact($details);
        }

        return new Response(ApiJson::encode($body), $status, ['Content-Type' => 'application/json; charset=utf-8']);
    }
}
