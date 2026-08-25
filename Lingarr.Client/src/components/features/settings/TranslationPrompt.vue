<template>
    <CardComponent title="Translation Prompt">
        <template #description>
            Define the prompts used when translating subtitle lines.
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
                    :required-placeholders="systemRequiredPlaceholders"
                    @update:validation="(val) => (isSystemPromptValid = val)" />

                <div v-if="useBatchTranslation !== 'true'" class="space-y-4">
                    <div class="flex flex-col space-x-2">
                        <span class="font-semibold">Translation user prompt</span>
                        Use context placeholders to include surrounding source lines. Use translated
                        context pairs to include already completed source/target pairs, which can
                        improve consistency and disambiguation for translation-specialized models.
                    </div>
                    <TextAreaComponent
                        v-model="aiUserPrompt"
                        :rows="10"
                        :min-height="100"
                        :placeholders="[
                            PLACEHOLDER.LINE_TO_TRANSLATE,
                            PLACEHOLDER.SOURCE_LANGUAGE,
                            PLACEHOLDER.TARGET_LANGUAGE,
                            PLACEHOLDER.CONTEXT_BEFORE,
                            PLACEHOLDER.CONTEXT_AFTER,
                            PLACEHOLDER.CONTEXT_PAIRS_BEFORE
                        ]"
                        :required-placeholders="['{lineToTranslate}']"
                        @update:validation="(val) => (isUserPromptValid = val)" />

                    <InputComponent
                        v-model="contextBefore"
                        :type="INPUT_TYPE.NUMBER"
                        :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                        label="Context before"
                        @update:validation="(val) => (isValid.contextBefore = val)" />
                    <p class="text-secondary-content/60 text-xs">
                        This count controls both {contextBefore} source-only history and
                        {contextPairsBefore} completed source/target history. Translated pair history
                        automatically skips duplicate subtitle rendering layers.
                    </p>
                    <InputComponent
                        v-model="contextAfter"
                        :type="INPUT_TYPE.NUMBER"
                        :validation-type="INPUT_VALIDATION_TYPE.NUMBER"
                        label="Context after"
                        @update:validation="(val) => (isValid.contextAfter = val)" />
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
import { computed, reactive, ref } from 'vue'
import { useSettingStore } from '@/store/setting'
import { INPUT_TYPE, INPUT_VALIDATION_TYPE, PLACEHOLDER, SETTINGS } from '@/ts'
import CardComponent from '@/components/common/CardComponent.vue'
import TextAreaComponent from '@/components/common/TextAreaComponent.vue'
import InputComponent from '@/components/common/InputComponent.vue'
import SaveNotification from '@/components/common/SaveNotification.vue'

const settingsStore = useSettingStore()
const saveNotification = ref<InstanceType<typeof SaveNotification> | null>(null)
const isSystemPromptValid = ref(true)
const isUserPromptValid = ref(false)
const isValid = reactive({
    contextBefore: true,
    contextAfter: true
})

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

const systemRequiredPlaceholders = computed(() =>
    aiPrompt.value.trim().length === 0 ? [] : ['{sourceLanguage}', '{targetLanguage}']
)

const systemPlaceholders = computed(() => {
    const items = [PLACEHOLDER.SOURCE_LANGUAGE, PLACEHOLDER.TARGET_LANGUAGE]
    if (useBatchTranslation.value !== 'true') {
        items.push(
            PLACEHOLDER.LINE_TO_TRANSLATE,
            PLACEHOLDER.CONTEXT_BEFORE,
            PLACEHOLDER.CONTEXT_AFTER,
            PLACEHOLDER.CONTEXT_PAIRS_BEFORE
        )
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

const contextBefore = computed({
    get: () => settingsStore.getSetting(SETTINGS.AI_CONTEXT_BEFORE) as string,
    set: (newValue: string) => {
        settingsStore.updateSetting(SETTINGS.AI_CONTEXT_BEFORE, newValue, isValid.contextBefore)
        if (isValid.contextBefore) {
            saveNotification.value?.show()
        }
    }
})

const contextAfter = computed({
    get: () => settingsStore.getSetting(SETTINGS.AI_CONTEXT_AFTER) as string,
    set: (newValue: string) => {
        settingsStore.updateSetting(SETTINGS.AI_CONTEXT_AFTER, newValue, isValid.contextAfter)
        if (isValid.contextAfter) {
            saveNotification.value?.show()
        }
    }
})
</script>
