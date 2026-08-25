import { AxiosError, AxiosResponse, AxiosStatic } from 'axios'
import { IResegmentationEvaluationRequest, IResegmentationService } from '@/ts'

const service = (http: AxiosStatic, resource = '/api/resegmentation'): IResegmentationService => ({
    evaluate<T>(request: IResegmentationEvaluationRequest): Promise<T> {
        return new Promise((resolve, reject) => {
            http.post(`${resource}/evaluate`, request)
                .then((response: AxiosResponse<T>) => {
                    resolve(response.data)
                })
                .catch((error: AxiosError) => {
                    reject(error.response)
                })
        })
    }
})

export const resegmentationService = (axios: AxiosStatic): IResegmentationService => {
    return service(axios)
}
