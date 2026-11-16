<?php

namespace Zouriel\Workflows\Tests\Support\App;

use Illuminate\Database\Eloquent\Model;
use Illuminate\Database\Eloquent\Relations\BelongsTo;
use Illuminate\Database\Eloquent\Relations\HasMany;
use Zouriel\Workflows\Laravel\Changes\HasWorkflows;

class Application extends Model
{
    use HasWorkflows;

    protected $guarded = [];

    public $timestamps = false;

    protected function casts(): array
    {
        return ['status' => ApplicationStatus::class, 'amount' => 'decimal:2', 'deadline' => 'date'];
    }

    public function programme(): BelongsTo
    {
        return $this->belongsTo(Programme::class);
    }

    public function documents(): HasMany
    {
        return $this->hasMany(Document::class);
    }
}
