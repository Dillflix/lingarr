export interface IResegmentationModelOverride {
    endpoint?: string
    model?: string
    apiKey?: string
    systemPrompt?: string
    userPrompt?: string
    timeoutSeconds?: number
}

export interface IResegmentationEvaluationRequest {
    sourceLanguage: string
    targetLanguage: string
    sourceSegments: string[]
    translatedUnit: string
    referenceSegments?: string[]
    mode?: 'deterministic' | 'model' | 'validated'
    modelOverride?: IResegmentationModelOverride
    validatorOverride?: IResegmentationModelOverride
}

export interface IResegmentationStructuralValidation {
    isValid: boolean
    countMatches: boolean
    nonEmptySegments: boolean
    textPreserved: boolean
    error?: string
}

export interface IResegmentationCandidate {
    method: string
    segments?: string[]
    validation: IResegmentationStructuralValidation
    error?: string
    latencyMs?: number
    model?: string
}

export interface IResegmentationValidatorDecision {
    winner: string
    modelScore: number
    deterministicScore: number
    reason?: string
    latencyMs?: number
    model?: string
}

export interface IResegmentationBoundaryMetrics {
    boundaryCount: number
    meanAbsoluteErrorCharacters: number
    maxAbsoluteErrorCharacters: number
    boundariesWithinFiveCharactersPercent: number
    exactSegmentMatchPercent: number
}

export interface IResegmentationEvaluationResult {
    mode: string
    selectedMethod: string
    selectedSegments: string[]
    deterministic: IResegmentationCandidate
    model?: IResegmentationCandidate
    validator?: IResegmentationValidatorDecision
    referenceValidation?: IResegmentationStructuralValidation
    deterministicReferenceMetrics?: IResegmentationBoundaryMetrics
    modelReferenceMetrics?: IResegmentationBoundaryMetrics
    fallbackReason?: string
}

export interface INamedBenchmarkModel {
    name?: string
    endpoint: string
    model: string
    apiKey?: string
    systemPrompt?: string
    userPrompt?: string
    timeoutSeconds?: number
}

export interface IResegmentationBenchmarkRunRequest {
    sampleLimit: number
    sampleIds?: number[]
    sourceLanguage?: string
    targetLanguage?: string
    candidateModels?: INamedBenchmarkModel[]
    judgeModels?: INamedBenchmarkModel[]
    backtranslationModel?: INamedBenchmarkModel
    includeAdversarialCalibration?: boolean
    autoHarvest?: boolean
    harvestRequestLimit?: number
}

export interface IResegmentationBenchmarkHarvestResult {
    requestsScanned: number
    multiCueUnitsFound: number
    newSamplesCaptured: number
    totalCorpusSamples: number
}

export interface IResegmentationBacktranslationMetrics {
    backtranslatedSegments: string[]
    meanSameSlotTokenF1Percent: number
    meanCrossSlotMarginPercentagePoints: number
    crossSlotLeakagePercent: number
    latencyMs?: number
}

export interface IResegmentationBenchmarkBaselineSummary {
    backtranslationSamples: number
    meanSameSlotTokenF1Percent?: number
    meanCrossSlotMarginPercentagePoints?: number
    crossSlotLeakagePercent?: number
}

export interface IResegmentationBenchmarkCandidateSummary {
    name: string
    model: string
    samplesAttempted: number
    structurallyValidSamples: number
    structuralValidityPercent: number
    meanAlignmentLatencyMs: number
    judgeModelVotes: number
    judgeDeterministicVotes: number
    judgeTies: number
    judgePreferencePercent: number
    meanJudgeAgreementPercent: number
    adversarialTrials: number
    adversarialPassPercent: number
    backtranslationSamples: number
    meanSameSlotTokenF1Percent?: number
    meanCrossSlotMarginPercentagePoints?: number
    crossSlotLeakagePercent?: number
}

export interface IResegmentationBenchmarkJudgeSummary {
    name: string
    model: string
    pairwiseComparisons: number
    decisiveComparisons: number
    adversarialTrials: number
    adversarialPassPercent: number
    meanLatencyMs: number
}

export interface IResegmentationBenchmarkCandidateResult {
    name: string
    model: string
    structurallyValid: boolean
    segments?: string[]
    error?: string
    alignmentLatencyMs?: number
    judgeModelVotes: number
    judgeDeterministicVotes: number
    judgeTies: number
    judgeAgreementPercent?: number
    adversarialTrials: number
    adversarialPasses: number
    backtranslation?: IResegmentationBacktranslationMetrics
}

export interface IResegmentationBenchmarkSampleResult {
    sampleId: number
    sourceLanguage: string
    targetLanguage: string
    sourceSegments: string[]
    translatedUnit: string
    deterministicSegments: string[]
    deterministicBacktranslation?: IResegmentationBacktranslationMetrics
    candidates: IResegmentationBenchmarkCandidateResult[]
}

export interface IResegmentationBenchmarkRunResult {
    sampleCount: number
    harvest?: IResegmentationBenchmarkHarvestResult
    deterministicBaseline: IResegmentationBenchmarkBaselineSummary
    candidates: IResegmentationBenchmarkCandidateSummary[]
    judges: IResegmentationBenchmarkJudgeSummary[]
    samples: IResegmentationBenchmarkSampleResult[]
    warnings: string[]
}
