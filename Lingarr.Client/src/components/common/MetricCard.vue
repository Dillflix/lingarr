<template>
    <div class="border-accent/30 rounded-md border p-3">
        <div class="text-secondary-content/60 text-xs">{{ label }}</div>
        <div class="font-semibold">{{ value }}</div>
    </div>
</template>

<script setup lang="ts">
defineProps<{
    label: string
    value: string | number
}>()
</script>
