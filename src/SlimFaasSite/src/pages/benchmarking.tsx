import DocumentationPage from '@/components/DocumentationPage';
import { createDocumentationStaticProps } from '@/lib/documentation';

export const getStaticProps = createDocumentationStaticProps('benchmarking');

export default DocumentationPage;
