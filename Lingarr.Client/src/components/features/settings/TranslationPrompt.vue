<template>
    <CardComponent title="Translation Prompt">
        <template #description>
            Define the prompts used when translating subtitle translation units.
        </template>
        <template #content>
            <div class="flex flex-col space-y-4">
                <SaveNotification ref="saveNotification" />
                <div class="flex flex-col space-x-2">
                    <span class="font-semibold">Translation system prompt</span>
                    Define the AI's behavior and tone by setting global instructions. This may be
                    left empty for translation-specialized models that expect a single user prompt.
                </div>
                <TextAreaComponent
                    v-model="aiPrompt"
                    :rows="10"
                    :min-height="100"
                    :placeholders="systemPlaceholders"
                    :required-placeholders="['{sourceLanguage}', '{targetLanguage}']"
                    allow-empty
                    @update:validation="(val) => (isSystemPromptValid = val)" />

                <div v-if="useBatchTranslation !== 'true'" class="space-y-4">
                    <div class="flex flex-col space-x-2">
                        <span class="font-semibold">Translation user prompt</span>
                        Lingarr automatically combines subtitle fragments that belong to the same
                        sentence or utterance. <code>{lineToTranslate}</code> therefore contains the
                        complete translation unit, whether that unit spans one cue or several cues.
                    </div>
                    <div class="border-accent/30 bg-accent/5 rounded-md border p-3 text-xs">
                        Do not add <code>{contextBefore}</code>, <code>{contextAfter}</code>, or
                        <code>{contextPairsBefore}</code> to this prompt. Sentence-aware translation
                        sends only text that should actually be translated, then resegments the
                        translated unit back onto the original subtitle timings.
                    </div>
                    <TextAreaComponent
                        v-model="aiUserPrompt"
                        :rows="10"
                        :min-height="100"
                        :placeholders="[
                            PLACEHOLDER.LINE_TO_TRANSLATE,
                            PLACEHOLDER.SOURCE_LANGUAGE,
                            PLACEHOLDER.TARGET_LANGUAGE
                        ]"
                        :required-placeholders="['{lineToTranslate}']"
                        @update:validation="(val) => (isUserPromptValid = val)" />
                </div>
                <div v-else class="text-xs">
                    The user prompt is not applied when sending subtitles in batch; the subtitle
                    batch is sent directly as the user message.
                </div>
            </div>
        </template>
    </CardComponent>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useSettingStore } from '@/store/setting'
import { PLACEHOLDER, SETTINGS } from '@/ts'
import CardComponent from '@/components/common/CardComponent.vue'
import TextAreaComponent from '@/components/common/TextAreaComponent.vue'
import SaveNotification from '@/components/common/SaveNotification.vue'

const settingsStore = useSettingStore()
const saveNotification = ref<InstanceType<typeof SaveNotification> | null>(null)
const isSystemPromptValid = ref(true)
const isUserPromptValid = ref(false)

const useBatchTranslation = computed(
    () => settingsStore.getSetting(SETTINGS.USE_BATCH_TRANSLATION) as string
)

const aiPrompt = computed({
    get: () => (settingsStore.getSetting(SETTINGS.AI_PROMPT) as string) ?? '',
    set: (newValue: string) => {
        settingsStore.updateSetting(SETTINGS.AI_PROMPT, newValue, isSystemPromptValid.value)
        if (isSystemPromptValid.value) {
            saveNotification.value?.show()
        }
    }
})

const systemPlaceholders = computed(() => {
    const items = [PLACEHOLDER.SOURCE_LANGUAGE, PLACEHOLDER.TARGET_LANGUAGE]
    if (useBatchTranslation.value !== 'true') {
        items.push(PLACEHOLDER.LINE_TO_TRANSLATE)
    }
    return items
})

const aiUserPrompt = computed({
    get: () => (settingsStore.getSetting(SETTINGS.AI_USER_PROMPT) as string) ?? '',
    set: (newValue: string) => {
        settingsStore.updateSetting(SETTINGS.AI_USER_PROMPT, newValue, isUserPromptValid.value)
        if (isUserPromptValid.value) {
            saveNotification.value?.show()
        }
    }
})
</script>
