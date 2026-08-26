export interface ISourceUnitDetectionCue {
    position: number
    startTime: number
    endTime: number
    text: string
}

export interface ISourceUnitBenchmarkModel {
    name?: string
    endpoint: string
    model: string
    apiKey?: string
    systemPrompt?: string
    userPrompt?: string
    timeoutSeconds?: number
}

export interface ISourceUnitBenchmarkRunRequest {
    sampleLimit: number
    sampleIds?: number[]
    sourceLanguage?: string
    candidateModels?: ISourceUnitBenchmarkModel[]
    judgeModels?: ISourceUnitBenchmarkModel[]
    includeAdversarialCalibration?: boolean
}

export interface ISourceUnitBenchmarkSampleView {
    id: number
    createdAt: string
    sourceLanguage: string
    cues: ISourceUnitDetectionCue[]
    candidateCount: number
    heuristicUnitLength: number
    productionMode?: string
    productionModelUnitLength?: number
    productionModelIsValid?: boolean
    productionModelError?: string
    productionModelLatencyMs?: number
    productionValidatorWinner?: string
    productionValidatorModel?: string
    productionValidatorModelScore?: number
    productionValidatorHeuristicScore?: number
    productionValidatorLatencyMs?: number
    productionSelectedUnitLength?: number
    productionSelectedMethod?: string
    translationRequestId?: number
    startPosition?: number
    endPosition?: number
}

export interface ISourceUnitBenchmarkCandidateSummary {
    name: string
    model: string
    samplesAttempted: number
    structurallyValidSamples: number
    structuralValidityPercent: number
    disagreementSamples: number
    disagreementPercent: number
    meanBoundaryLatencyMs: number
    judgeModelVotes: number
    judgeHeuristicVotes: number
    judgeTies: number
    judgePreferencePercent: number
    meanJudgeAgreementPercent: number
    adversarialTrials: number
    adversarialPassPercent: number
}

export interface ISourceUnitBenchmarkJudgeSummary {
    name: string
    model: string
    pairwiseComparisons: number
    decisiveComparisons: number
    adversarialTrials: number
    adversarialPassPercent: number
    meanLatencyMs: number
}

export interface ISourceUnitBenchmarkCandidateResult {
    name: string
    model: string
    structurallyValid: boolean
    unitLength?: number
    error?: string
    boundaryLatencyMs?: number
    disagreesWithHeuristic: boolean
    judgeModelVotes: number
    judgeHeuristicVotes: number
    judgeTies: number
    judgeAgreementPercent?: number
    adversarialTrials: number
    adversarialPasses: number
}

export interface ISourceUnitBenchmarkSampleResult {
    sampleId: number
    sourceLanguage: string
    cues: ISourceUnitDetectionCue[]
    heuristicUnitLength: number
    capturedProductionModelUnitLength?: number
    capturedProductionSelectedUnitLength?: number
    capturedProductionSelectedMethod?: string
    candidates: ISourceUnitBenchmarkCandidateResult[]
}

export interface ISourceUnitBenchmarkRunResult {
    sampleCount: number
    candidates: ISourceUnitBenchmarkCandidateSummary[]
    judges: ISourceUnitBenchmarkJudgeSummary[]
    samples: ISourceUnitBenchmarkSampleResult[]
    warnings: string[]
}
